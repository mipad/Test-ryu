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
            public string FullTypeName { get; set; }
            public string TypeName { get; set; }
            public string Namespace { get; set; }
            public List<ConstructorInfo> Constructors { get; set; } = new List<ConstructorInfo>();
        }

        private class ConstructorInfo
        {
            public List<ParameterInfo> Parameters { get; set; } = new List<ParameterInfo>();
        }

        private class ParameterInfo
        {
            public string TypeName { get; set; }
            public string FullTypeName { get; set; }
            public string Name { get; set; }
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var syntaxReceiver = new DeviceFileSyntaxReceiver();
            var compilation = context.Compilation;
            
            // 遍历所有语法树
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var root = syntaxTree.GetRoot();
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                
                // 查找所有继承自NvDeviceFile的类
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
                        if (baseType.Name == "NvDeviceFile")
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

                    var deviceFileConstructors = new List<ConstructorInfo>();

                    foreach (var constructor in constructors)
                    {
                        var parameters = constructor.ParameterList.Parameters;
                        
                        // 检查是否是 ServiceCtx, IVirtualMemoryManager, ulong 构造函数
                        if (parameters.Count == 3)
                        {
                            var paramTypes = parameters.Select(p => 
                                semanticModel.GetTypeInfo(p.Type).Type?.ToDisplayString() ?? p.Type.ToString()).ToList();
                            
                            if (paramTypes[0].Contains("ServiceCtx") && 
                                paramTypes[1].Contains("IVirtualMemoryManager") && 
                                paramTypes[2] == "ulong")
                            {
                                var ctorInfo = new ConstructorInfo();
                                foreach (var parameter in parameters)
                                {
                                    var paramTypeSymbol = semanticModel.GetSymbolInfo(parameter.Type).Symbol as INamedTypeSymbol;
                                    ctorInfo.Parameters.Add(new ParameterInfo
                                    {
                                        TypeName = parameter.Type.ToString(),
                                        FullTypeName = paramTypeSymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? parameter.Type.ToString(),
                                        Name = parameter.Identifier.Text
                                    });
                                }
                                deviceFileConstructors.Add(ctorInfo);
                            }
                        }
                    }

                    if (deviceFileConstructors.Any())
                    {
                        var deviceFileInfo = new DeviceFileInfo
                        {
                            FullTypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", ""),
                            TypeName = classSymbol.Name,
                            Namespace = classSymbol.ContainingNamespace.ToDisplayString(),
                            Constructors = deviceFileConstructors
                        };
                        
                        syntaxReceiver.DeviceFiles.Add(deviceFileInfo);
                    }
                }
            }

            // 生成设备文件工厂代码
            if (syntaxReceiver.DeviceFiles.Any())
            {
                var generator = new CodeGenerator();
                
                generator.AppendLine("using Ryujinx.HLE.HOS.Ipc;");
                generator.AppendLine("using Ryujinx.Cpu;");
                generator.AppendLine("using System;");
                generator.AppendLine("using System.Collections.Generic;");
                generator.AppendLine();
                
                generator.EnterScope("namespace Ryujinx.HLE.Generators");
                generator.EnterScope("internal static class DeviceFileFactory");
                generator.EnterScope("public static NvDeviceFile CreateDeviceFile(string path, ServiceCtx context, IVirtualMemoryManager memory, ulong owner)");
                
                generator.EnterScope("switch (path)");
                
                foreach (var deviceFile in syntaxReceiver.DeviceFiles)
                {
                    generator.EnterScope($"case \"{GetDevicePath(deviceFile.TypeName)}\":");
                    generator.AppendLine($"return new {deviceFile.FullTypeName}(context, memory, owner);");
                    generator.LeaveScope();
                }
                
                generator.AppendLine("default:");
                generator.AppendLine("    return null;");
                generator.LeaveScope();
                
                generator.LeaveScope();
                generator.LeaveScope();
                generator.LeaveScope();
                
                context.AddSource("DeviceFileFactory.g.cs", generator.ToString());
            }
        }

        private string GetDevicePath(string typeName)
        {
            // 将类型名转换为设备路径
            // 例如: NvMapDeviceFile -> "/dev/nvmap"
            if (typeName.StartsWith("Nv"))
                typeName = typeName.Substring(2);
            
            if (typeName.EndsWith("DeviceFile"))
                typeName = typeName.Substring(0, typeName.Length - "DeviceFile".Length);
            
            // 转换为小写并用连字符分隔
            var result = new StringBuilder();
            for (int i = 0; i < typeName.Length; i++)
            {
                if (i > 0 && char.IsUpper(typeName[i]))
                    result.Append('-');
                result.Append(char.ToLower(typeName[i]));
            }
            
            return $"/dev/nvhost-{result}";
        }

        public void Initialize(GeneratorInitializationContext context)
        {
            // 不需要注册语法接收器，因为我们直接分析所有语法树
        }

        private class DeviceFileSyntaxReceiver
        {
            public List<DeviceFileInfo> DeviceFiles { get; } = new List<DeviceFileInfo>();
        }
    }
}
