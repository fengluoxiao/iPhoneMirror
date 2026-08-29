#include "Transport/MuxForward.h"

#include "Logging.h"
#include "Transport/Socket.h"
#include "Transport/UsbMuxClient.h"

#include <WS2tcpip.h>
#include <WinSock2.h>

#include <array>
#include <atomic>
#include <chrono>
#include <format>
#include <map>
#include <memory>
#include <mutex>
#include <thread>

namespace iPhoneMirror::transport {
namespace {

constexpr std::uint16_t KnownMuxPorts[] = {27015, 37015};
constexpr std::size_t MaxActiveConnections = 16;

struct ForwardState {
    std::atomic<bool> stopping{false};
    std::atomic<int> active_connections{0};
    SOCKET listener{INVALID_SOCKET};
    std::string udid;
    std::uint16_t device_port{};
};

std::mutex registry_mutex;
std::map<std::uint16_t, std::shared_ptr<ForwardState>> registry;

std::string narrow_udid(const std::wstring& udid) {
    // UDIDs are ASCII hex; a naive narrowing is exact for every real value.
    return std::string(udid.begin(), udid.end());
}

// Resolves the usbmux device for the stored UDID and opens a tunnel to the
// requested port. Discovery happens per connection: the device legitimately
// disappears from usbmux while QuickTime re-enumerates it and its usbmux
// device id changes on every replug, so a snapshot taken at listener start
// would go stale. found_device distinguishes "no such device in usbmux"
// from "device present but the target port refused the tunnel".
bool resolve_and_connect(const ForwardState& state, Socket& tunnel,
                         bool* found_device) {
    *found_device = false;
    const std::uint16_t* end = KnownMuxPorts + std::size(KnownMuxPorts);
    for (const std::uint16_t* port = KnownMuxPorts; port != end; ++port) {
        try {
            UsbMuxClient mux(*port);
            for (const auto& device : mux.list_devices()) {
                if (device.serial != state.udid) continue;
                *found_device = true;
                tunnel = mux.connect_device(device.device_id, state.device_port);
                return true;
            }
        } catch (...) {
            // Port not published or transient IPC failure: try the next one.
        }
    }
    return false;
}

void send_all(SOCKET handle, const char* data, int length) {
    int offset = 0;
    while (offset < length) {
        const int sent = ::send(handle, data + offset, length - offset, 0);
        if (sent == SOCKET_ERROR || sent == 0) throw SocketError("send", WSAGetLastError());
        offset += sent;
    }
}

void relay_connection(std::shared_ptr<ForwardState> state, SOCKET client) {
    state->active_connections.fetch_add(1);
    try {
        Socket tunnel;
        bool found_device = false;
        if (!resolve_and_connect(*state, tunnel, &found_device)) {
            // The device is absent from usbmux (replug, QuickTime
            // re-enumeration) or WDA is not listening yet. Report the state
            // of the mux list once every few seconds instead of per retry.
            static std::atomic<std::int64_t> last_report_ms{};
            const auto now_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now().time_since_epoch()).count();
            auto previous = last_report_ms.load(std::memory_order_relaxed);
            if (now_ms - previous > 5000 &&
                last_report_ms.compare_exchange_strong(previous, now_ms)) {
                std::string serials;
                try {
                    for (const std::uint16_t port : KnownMuxPorts) {
                        UsbMuxClient mux(port);
                        for (const auto& device : mux.list_devices()) {
                            serials += device.serial + " ";
                        }
                    }
                } catch (...) {}
                logging::write(found_device
                    ? std::format("mux_forward connect_failed udid={} (device present; target port refused)",
                        state->udid)
                    : std::format("mux_forward device_not_found udid={} mux_devices=[{}]",
                        state->udid, serials));
            }
            ::closesocket(client);
            state->active_connections.fetch_sub(1);
            return;
        }
        // The tunnel carries traffic for the whole WDA session; drop the
        // 1.5s handshake timeout connect_device installs.
        tunnel.set_timeout(0);

        const SOCKET device_handle = tunnel.native_handle();
        std::array<char, 16 * 1024> buffer{};
        for (;;) {
            fd_set read_set;
            FD_ZERO(&read_set);
            FD_SET(client, &read_set);
            FD_SET(device_handle, &read_set);
            const int ready = ::select(0, &read_set, nullptr, nullptr, nullptr);
            if (ready <= 0) break;
            if (FD_ISSET(client, &read_set)) {
                const int received = ::recv(client, buffer.data(),
                    static_cast<int>(buffer.size()), 0);
                if (received <= 0) break;
                send_all(device_handle, buffer.data(), received);
            }
            if (FD_ISSET(device_handle, &read_set)) {
                const int received = ::recv(device_handle, buffer.data(),
                    static_cast<int>(buffer.size()), 0);
                if (received <= 0) break;
                send_all(client, buffer.data(), received);
            }
        }
    } catch (...) {
        // Tunnel setup or relay I/O failure simply ends this connection; the
        // HTTP client retries with a fresh one on its next request.
    }
    ::closesocket(client);
    state->active_connections.fetch_sub(1);
}

void accept_loop(std::shared_ptr<ForwardState> state) {
    while (!state->stopping.load()) {
        sockaddr_in client_address{};
        int client_length = sizeof(client_address);
        const SOCKET client = ::accept(state->listener,
            reinterpret_cast<sockaddr*>(&client_address), &client_length);
        if (client == INVALID_SOCKET) break;
        if (state->stopping.load()) {
            ::closesocket(client);
            break;
        }
        if (state->active_connections.load() >= MaxActiveConnections) {
            ::closesocket(client);
            continue;
        }
        std::thread(relay_connection, state, client).detach();
    }
    ::closesocket(state->listener);
    state->listener = INVALID_SOCKET;
}

} // namespace

std::int32_t MuxForward::start(const std::wstring& udid, std::uint16_t device_port,
                               std::uint16_t* local_port) {
    if (udid.empty() || device_port == 0 || local_port == nullptr) {
        return static_cast<std::int32_t>(MuxForwardResult::InvalidArgument);
    }
    try {
        ensure_winsock();
        const SOCKET listener = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (listener == INVALID_SOCKET) {
            return static_cast<std::int32_t>(MuxForwardResult::TransportUnavailable);
        }
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = 0;
        if (::bind(listener, reinterpret_cast<const sockaddr*>(&address),
                sizeof(address)) == SOCKET_ERROR ||
            ::listen(listener, 8) == SOCKET_ERROR) {
            ::closesocket(listener);
            return static_cast<std::int32_t>(MuxForwardResult::TransportUnavailable);
        }
        sockaddr_in bound{};
        int bound_length = sizeof(bound);
        if (::getsockname(listener, reinterpret_cast<sockaddr*>(&bound),
                &bound_length) == SOCKET_ERROR) {
            ::closesocket(listener);
            return static_cast<std::int32_t>(MuxForwardResult::TransportUnavailable);
        }

        auto state = std::make_shared<ForwardState>();
        state->listener = listener;
        state->udid = narrow_udid(udid);
        state->device_port = device_port;
        const std::uint16_t bound_port = ntohs(bound.sin_port);

        {
            std::lock_guard lock(registry_mutex);
            registry[bound_port] = state;
        }
        std::thread(accept_loop, state).detach();
        *local_port = bound_port;
        return static_cast<std::int32_t>(MuxForwardResult::Ok);
    } catch (...) {
        return static_cast<std::int32_t>(MuxForwardResult::TransportUnavailable);
    }
}

void MuxForward::stop(std::uint16_t local_port) {
    std::shared_ptr<ForwardState> state;
    {
        std::lock_guard lock(registry_mutex);
        const auto entry = registry.find(local_port);
        if (entry == registry.end()) return;
        state = entry->second;
        registry.erase(entry);
    }
    state->stopping.store(true);
    if (state->listener != INVALID_SOCKET) {
        ::closesocket(state->listener);
        state->listener = INVALID_SOCKET;
    }
}

} // namespace iPhoneMirror::transport
