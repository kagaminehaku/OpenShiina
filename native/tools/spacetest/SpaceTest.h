// spacetest: ScnAddressSpace checked against a plain model of the 4 GB (a map of the bytes written),
// over random writes, reads and discards in the regions the VM uses, accesses across parts and at the
// top of the 4 GB, and parts made by several threads at once; then its speed.
#pragma once

#include <functional>
#include <string>

namespace openshiina::tools {

/// The number of checks that failed; each one, and the results, go to log.
int RunSpaceTest(const std::function<void(const std::string&)>& log);

} // namespace openshiina::tools
