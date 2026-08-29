#pragma once

#include <cstdint>
#include <string>

namespace iPhoneMirror::transport {

// Bridges loopback TCP connections to a device TCP port through usbmuxd,
// one usbmux tunnel per accepted client (iproxy semantics). The wired-control
// feature uses this to reach WebDriverAgent on device port 8100 while the
// QuickTime capture keeps its own usbmux sessions on the same link.
enum class MuxForwardResult : std::int32_t {
    Ok = 0,
    InvalidArgument = -1,
    TransportUnavailable = -4,
    DeviceNotFound = -6,
};

class MuxForward {
public:
    // Resolves the usbmux device by UDID, binds a loopback listener and runs
    // the accept loop on a detached thread. Returns Ok and the bound local
    // port on success.
    static std::int32_t start(const std::wstring& udid, std::uint16_t device_port,
                              std::uint16_t* local_port);
    // Closes the listener and unregisters the forward. Existing tunnels are
    // released when their clients disconnect.
    static void stop(std::uint16_t local_port);
};

} // namespace iPhoneMirror::transport
