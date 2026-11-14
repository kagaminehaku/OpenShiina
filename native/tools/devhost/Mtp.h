// devhost's MTP responder (Mtp.cpp): the Switch as an MTP device over its USB port, as DBI's "Run
// MTP responder" makes it, so that Windows' own MTP driver shows sdmc:/OpenShiina in Explorer (no
// driver to install) and files go over the cable while devhost goes on taking the network's.
#pragma once

#include <atomic>
#include <functional>
#include <string>

namespace openshiina::devhost {

/// Starts the responder on a thread of its own. log: a line for devhost's screen; busy: counted up
/// while a file is read or written (devhost waits for it before starting a program or leaving).
/// False when the USB device could not be set up (told through log).
bool StartMtp(std::function<void(const std::string&)> log, std::atomic<int>* busy);

} // namespace openshiina::devhost
