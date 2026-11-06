// spacetest on the PC: the results on stdout; exit code 1 when a check failed.
#include <cstdio>

#include "SpaceTest.h"

int main()
{
    int failed = openshiina::tools::RunSpaceTest([](const std::string& line) {
        std::printf("%s\n", line.c_str());
        std::fflush(stdout);
    });
    return failed == 0 ? 0 : 1;
}
