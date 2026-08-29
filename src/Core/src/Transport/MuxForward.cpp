#include "Transport/MuxForward.h"

#include "Transport/Socket.h"
#include "Transport/UsbMuxClient.h"

#include <WS2tcpip.h>
#include <WinSock2.h>

#include <array>
#include <atomic>
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
    std::uint32_t device_id{};
    std::uint16_t mux_port{};
    std::uint16_t device_port{};
};

std::mutex registry_mutex;
std::map<std::uint16_t, std::shared_ptr<ForwardState>> registry;

std::string narrow_udid(const std::wstring& udid) {
    // UDIDs are ASCII hex; a naive narrowing is exact for every real value.
    return std::string(udid.begin(), udid.end());
}

bool find_device(const std::wstring& udid, std::uint16_t* mux_port,
                 std::uint32_t* device_id) {
    const std::string serial = narrow_udid(udid);
    for (const std::uint16_t port : KnownMuxPorts) {
        try {
            UsbMuxClient mux(port);
            for (const auto& device : mux.list_devices()) {
                if (device.serial == serial) {
                    *mux_port = port;
                    *device_id = device.device_id;
                    return true;
                }
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
        UsbMuxClient mux(state->mux_port);
        // connect_device applies htons internally and expects host order.
        Socket device = mux.connect_device(state->device_id, state->device_port);
        // The tunnel carries traffic for the whole WDA session; drop the
        // 1.5s handshake timeout connect_device installs.
        device.set_timeout(0);

        const SOCKET device_handle = device.native_handle();
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
        std::uint16_t mux_port{};
        std::uint32_t device_id{};
        if (!find_device(udid, &mux_port, &device_id)) {
            return static_cast<std::int32_t>(MuxForwardResult::DeviceNotFound);
        }

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
        state->device_id = device_id;
        state->mux_port = mux_port;
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
