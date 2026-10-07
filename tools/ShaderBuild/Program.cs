// shaderbuild [folder]: compiles every GLSL compute shader (*.comp) of the folder (by default
// src/OpenShiina.Gpu/Shaders of the repository this runs from) to SPIR-V for Vulkan 1.0, next
// to it as *.spv. A shader that does not compile stops the run with shaderc's message.

using System.Text;
using Silk.NET.Shaderc;

string? folder = args.Length > 0 ? args[0] : null;
for (var d = new DirectoryInfo(AppContext.BaseDirectory); folder == null && d != null; d = d.Parent)
    if (Directory.Exists(Path.Combine(d.FullName, "src", "OpenShiina.Gpu", "Shaders")))
        folder = Path.Combine(d.FullName, "src", "OpenShiina.Gpu", "Shaders");
if (folder == null || !Directory.Exists(folder))
{
    Console.WriteLine("No shader folder.");
    return 1;
}

var shaderc = Shaderc.GetApi();
unsafe
{
    var compiler = shaderc.CompilerInitialize();
    var options = shaderc.CompileOptionsInitialize();
    shaderc.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan10);
    shaderc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
    try
    {
        foreach (string file in Directory.GetFiles(folder, "*.comp").Order())
        {
            byte[] source = File.ReadAllBytes(file);
            byte[] name = Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\0");
            byte[] entry = "main\0"u8.ToArray();
            CompilationResult* result;
            fixed (byte* s = source, n = name, e = entry)
                result = shaderc.CompileIntoSpv(compiler, s, (nuint)source.Length, ShaderKind.ComputeShader, n, e, options);
            try
            {
                if (shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                {
                    Console.WriteLine($"{Path.GetFileName(file)}: {shaderc.ResultGetErrorMessageS(result)}");
                    return 1;
                }
                var spirv = new ReadOnlySpan<byte>(shaderc.ResultGetBytes(result), (int)shaderc.ResultGetLength(result));
                string output = Path.ChangeExtension(file, ".spv");
                File.WriteAllBytes(output, spirv.ToArray());
                Console.WriteLine($"{Path.GetFileName(file)} -> {Path.GetFileName(output)} ({spirv.Length} bytes)");
            }
            finally
            {
                shaderc.ResultRelease(result);
            }
        }
    }
    finally
    {
        shaderc.CompileOptionsRelease(options);
        shaderc.CompilerRelease(compiler);
    }
}
return 0;
