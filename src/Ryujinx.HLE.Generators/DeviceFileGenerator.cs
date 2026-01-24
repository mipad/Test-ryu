using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ryujinx.HLE.Generators
{
    [Generator]
    public class DeviceFileGenerator : ISourceGenerator
    {
        private class DeviceFileInfo
        {
            public string? FullTypeName { get; set; }  // 修复: 标记为可空
            public string? TypeName { get; set; }      // 修复: 标记为可空
            public string? Namespace { get; set; }     // 修复: 标记为可空
            public bool HasValidConstructor { get; set; }
            public List<string> ConstructorParameters { get; set; } = new List<string>();
        }

        public void Execute(GeneratorExecutionContext context)
        {
            // 首先检查是否有任何继承自NvDeviceFile的类
            var compilation = context.Compilation;
            var deviceFileType = compilation.GetTypeByMetadataName("Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvDeviceFile");
            
            if (deviceFileType == null)
            {
                // 如果找不到基类，可能是项目引用问题
                return;
            }

            var deviceFileInfos = new List<DeviceFileInfo>();
            
            // 遍历所有语法树
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var root = syntaxTree.GetRoot();
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                
                // 查找所有类声明
                var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
                
                foreach (var classDeclaration in classDeclarations)
                {
                    var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration);
                    if (classSymbol == null)
                        continue;

                    // 检查是否继承自NvDeviceFile
                    var baseType = classSymbol.BaseType;
                    bool isNvDeviceFile = false;
                    
                    while (baseType != null)
                    {
                        if (baseType.Equals(deviceFileType, SymbolEqualityComparer.Default))
                        {
                            isNvDeviceFile = true;
                            break;
                        }
                        baseType = baseType.BaseType;
                    }

                    if (!isNvDeviceFile)
                        continue;

                    // 检查是否有合适的构造函数
                    var constructors = classDeclaration.ChildNodes()
                        .OfType<ConstructorDeclarationSyntax>()
                        .Where(c => !c.Modifiers.Any(SyntaxKind.AbstractKeyword))
                        .ToList();

                    bool hasValidConstructor = false;
                    List<string> constructorParams = new List<string>();
                    
                    foreach (var constructor in constructors)
                    {
                        var parameters = constructor.ParameterList.Parameters;
                        
                        // 检查参数数量
                        if (parameters.Count >= 2)
                        {
                            var paramTypes = new List<string>();
                            foreach (var parameter in parameters)
                            {
                                var paramTypeSymbol = semanticModel.GetTypeInfo(parameter.Type!).Type;  // 修复: 添加 ! 断言不为null
                                if (paramTypeSymbol != null)
                                {
                                    paramTypes.Add(paramTypeSymbol.ToDisplayString());
                                }
                            }
                            
                            // 检查是否是 ServiceCtx, IVirtualMemoryManager, ulong 构造函数
                            // 或者 ServiceCtx, ulong 构造函数（如NvMapDeviceFile）
                            if (paramTypes.Count >= 2)
                            {
                                // 检查第一个参数是否为ServiceCtx
                                if (paramTypes[0].Contains("ServiceCtx") || paramTypes[0].EndsWith("ServiceCtx"))
                                {
                                    hasValidConstructor = true;
                                    constructorParams.AddRange(paramTypes);
                                    break;
                                }
                            }
                        }
                    }

                    var deviceFileInfo = new DeviceFileInfo
                    {
                        FullTypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", ""),
                        TypeName = classSymbol.Name,
                        Namespace = classSymbol.ContainingNamespace.ToDisplayString(),
                        HasValidConstructor = hasValidConstructor,
                        ConstructorParameters = constructorParams
                    };
                    
                    deviceFileInfos.Add(deviceFileInfo);
                }
            }

            // 生成设备文件工厂代码
            if (deviceFileInfos.Any())
            {
                var generator = new CodeGenerator();
                
                // 添加必要的 using 指令
                generator.AppendLine("using Ryujinx.HLE.HOS;");
                generator.AppendLine("using Ryujinx.Memory;");
                generator.AppendLine("using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices;");
                generator.AppendLine();
                
                generator.EnterScope("namespace Ryujinx.HLE.Generators");
                generator.EnterScope("internal static partial class DeviceFileFactory");
                
                // 生成方法注释
                generator.AppendLine("/// <summary>");
                generator.AppendLine("/// Creates a device file instance based on the path.");
                generator.AppendLine("/// </summary>");
                generator.AppendLine("/// <param name=\"path\">The device path.</param>");
                generator.AppendLine("/// <param name=\"context\">The service context.</param>");
                generator.AppendLine("/// <param name=\"memory\">The virtual memory manager.</param>");
                generator.AppendLine("/// <param name=\"owner\">The owner process ID.</param>");
                generator.AppendLine("/// <returns>The created device file or null if not found.</returns>");
                
                generator.EnterScope("public static NvDeviceFile? CreateDeviceFile(string path, ServiceCtx context, IVirtualMemoryManager memory, ulong owner)");  // 修复: 返回类型可空
                
                generator.EnterScope("switch (path)");
                
                foreach (var deviceFile in deviceFileInfos)
                {
                    if (!deviceFile.HasValidConstructor || string.IsNullOrEmpty(deviceFile.FullTypeName))
                        continue;
                    
                    // 根据类型名生成设备路径
                    string? devicePath = GetDevicePathFromTypeName(deviceFile.TypeName!);
                    
                    if (!string.IsNullOrEmpty(devicePath))
                    {
                        generator.EnterScope($"case \"{devicePath}\":");
                        
                        // 根据构造函数的参数数量决定如何调用
                        if (deviceFile.ConstructorParameters.Count == 3 && 
                            (deviceFile.ConstructorParameters[1].Contains("IVirtualMemoryManager") || 
                             deviceFile.ConstructorParameters[1].EndsWith("IVirtualMemoryManager")))
                        {
                            generator.AppendLine($"return new {deviceFile.FullTypeName}(context, memory, owner);");
                        }
                        else if (deviceFile.ConstructorParameters.Count == 2)
                        {
                            // 只有ServiceCtx和owner（如NvMapDeviceFile）
                            generator.AppendLine($"return new {deviceFile.FullTypeName}(context, owner);");
                        }
                        else
                        {
                            // 其他构造函数，尝试使用默认值
                            generator.AppendLine($"// Warning: Unsupported constructor for {deviceFile.TypeName}");
                            generator.AppendLine("return null;");
                        }
                        
                        generator.LeaveScope();
                    }
                }
                
                // 添加注释掉的设备文件
                generator.EnterScope("// Note: The following devices are commented out in the original registry:");
                generator.AppendLine("// case \"/dev/nvhost-msenc\":");
                generator.AppendLine("// case \"/dev/nvhost-nvjpg\":");
                generator.AppendLine("// case \"/dev/nvhost-display\":");
                generator.AppendLine("//     return new Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel.NvHostChannelDeviceFile(context, memory, owner);");
                generator.LeaveScope();
                
                generator.AppendLine("default:");
                generator.AppendLine("    return null;");
                generator.LeaveScope();
                
                generator.LeaveScope();
                generator.LeaveScope();
                generator.LeaveScope();
                
                context.AddSource("DeviceFileFactory.g.cs", generator.ToString());
            }
        }

        private string? GetDevicePathFromTypeName(string typeName)  // 修复: 返回类型标记为可空
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            // 根据已知的设备文件类型名生成路径
            var pathMappings = new Dictionary<string, string>
            {
                { "NvMapDeviceFile", "/dev/nvmap" },
                { "NvHostCtrlDeviceFile", "/dev/nvhost-ctrl" },
                { "NvHostCtrlGpuDeviceFile", "/dev/nvhost-ctrl-gpu" },
                { "NvHostAsGpuDeviceFile", "/dev/nvhost-as-gpu" },
                { "NvHostGpuDeviceFile", "/dev/nvhost-gpu" },
                { "NvHostChannelDeviceFile", "/dev/nvhost-channel" }, // 基本路径，实际有多个实例
                { "NvHostDbgGpuDeviceFile", "/dev/nvhost-dbg-gpu" },
                { "NvHostProfGpuDeviceFile", "/dev/nvhost-prof-gpu" },
            };
            
            if (pathMappings.TryGetValue(typeName, out string? path))
            {
                return path;
            }
            
            // 如果不在映射中，尝试生成默认路径
            if (typeName.StartsWith("NvHost") && typeName.EndsWith("DeviceFile"))
            {
                string baseName = typeName.Substring(6, typeName.Length - 6 - "DeviceFile".Length);
                return $"/dev/nvhost-{baseName.ToLower()}";
            }
            
            return null;
        }

        public void Initialize(GeneratorInitializationContext context)
        {
            // 不需要注册语法接收器，因为我们直接分析所有语法树
        }
    }
}