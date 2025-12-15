using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.HLE.Generators
{
    [Generator]
    public class IpcServiceGenerator : ISourceGenerator
    {
        private class ServiceInfo
        {
            public string FullTypeName { get; set; }
            public string ServiceName { get; set; }
            public List<ConstructorInfo> Constructors { get; set; } = new List<ConstructorInfo>();
            public List<ConstructorInfo> ServiceCtxConstructors { get; set; } = new List<ConstructorInfo>();
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
            var syntaxReceiver = (ServiceSyntaxReceiver)context.SyntaxReceiver;
            
            // 收集所有服务类的信息
            var serviceInfos = new List<ServiceInfo>();
            
            foreach (var classDeclaration in syntaxReceiver.Types)
            {
                // 跳过抽象类和私有类
                if (classDeclaration.Modifiers.Any(SyntaxKind.AbstractKeyword) || 
                    classDeclaration.Modifiers.Any(SyntaxKind.PrivateKeyword))
                {
                    continue;
                }

                // 检查是否有ServiceAttribute
                var serviceAttributes = classDeclaration.AttributeLists
                    .SelectMany(x => x.Attributes)
                    .Where(y => 
                        y.Name.ToString() == "Service" || 
                        y.Name.ToString() == "ServiceAttribute")
                    .ToList();
                    
                if (serviceAttributes.Count == 0)
                {
                    continue;
                }

                // 获取完整类型名
                var fullTypeName = GetFullName(classDeclaration, context);

                // 获取所有构造函数信息
                var constructors = classDeclaration.ChildNodes()
                    .OfType<ConstructorDeclarationSyntax>()
                    .ToList();

                var serviceCtxConstructors = new List<ConstructorInfo>();

                foreach (var constructor in constructors)
                {
                    var ctorInfo = new ConstructorInfo();
                    var semanticModel = context.Compilation.GetSemanticModel(constructor.SyntaxTree);
                    
                    foreach (var parameter in constructor.ParameterList.Parameters)
                    {
                        var paramTypeSymbol = semanticModel.GetSymbolInfo(parameter.Type).Symbol as INamedTypeSymbol;
                        var paramInfo = new ParameterInfo
                        {
                            TypeName = parameter.Type.ToString(),
                            FullTypeName = paramTypeSymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? parameter.Type.ToString(),
                            Name = parameter.Identifier.Text
                        };
                        
                        ctorInfo.Parameters.Add(paramInfo);
                    }
                    
                    // 检查是否有ServiceCtx作为第一个参数
                    if (ctorInfo.Parameters.Count > 0 && 
                        (ctorInfo.Parameters[0].FullTypeName.Contains("ServiceCtx") || 
                         ctorInfo.Parameters[0].TypeName == "ServiceCtx"))
                    {
                        serviceCtxConstructors.Add(ctorInfo);
                    }
                }

                // 为每个ServiceAttribute创建一个服务条目
                foreach (var serviceAttribute in serviceAttributes)
                {
                    // 获取服务名称
                    string serviceName = "";
                    if (serviceAttribute.ArgumentList != null && serviceAttribute.ArgumentList.Arguments.Count > 0)
                    {
                        var firstArg = serviceAttribute.ArgumentList.Arguments[0];
                        if (firstArg.Expression is LiteralExpressionSyntax literal &&
                            literal.Kind() == SyntaxKind.StringLiteralExpression)
                        {
                            serviceName = literal.Token.ValueText;
                        }
                    }

                    if (string.IsNullOrEmpty(serviceName))
                    {
                        continue;
                    }

                    var serviceInfo = new ServiceInfo
                    {
                        FullTypeName = fullTypeName,
                        ServiceName = serviceName,
                        ServiceCtxConstructors = serviceCtxConstructors.ToList()
                    };

                    serviceInfos.Add(serviceInfo);
                }
            }

            // 生成代码
            CodeGenerator generator = new CodeGenerator();
            
            // 添加必要的using指令
            generator.AppendLine("using System;");
            generator.AppendLine("using System.Collections.Generic;");
            
            generator.EnterScope($"namespace Ryujinx.HLE.HOS.Services.Sm");
            generator.EnterScope($"partial class IUserInterface");
            
            // 生成_services字段
            generator.AppendLine("private static readonly Dictionary<string, Type> _services = BuildServiceDictionary();");
            
            // 生成BuildServiceDictionary方法
            generator.EnterScope($"private static Dictionary<string, Type> BuildServiceDictionary()");
            generator.EnterScope($"return new Dictionary<string, Type>");
            
            foreach (var serviceInfo in serviceInfos)
            {
                generator.AppendLine($"{{ \"{serviceInfo.ServiceName}\", typeof({serviceInfo.FullTypeName}) }},");
            }
            
            generator.LeaveScope(";");
            generator.LeaveScope();
            
            // 生成GetServiceInstance方法
            generator.EnterScope($"private IpcService GetServiceInstance(Type type, ServiceCtx context, object parameter)");
            
            // 按类型分组，避免重复生成相同的创建代码
            var groupedByType = serviceInfos.GroupBy(s => s.FullTypeName);

            foreach (var group in groupedByType)
            {
                var serviceInfo = group.First();
                generator.EnterScope($"if (type == typeof({serviceInfo.FullTypeName}))");
                
                // 检查是否有以ServiceCtx作为第一个参数的构造函数
                if (serviceInfo.ServiceCtxConstructors.Any())
                {
                    // 优先选择只有一个参数的构造函数（只有ServiceCtx）
                    var singleParamCtor = serviceInfo.ServiceCtxConstructors.FirstOrDefault(c => c.Parameters.Count == 1);
                    if (singleParamCtor != null)
                    {
                        generator.AppendLine($"return new {serviceInfo.FullTypeName}(context);");
                    }
                    else
                    {
                        // 检查是否有两个参数的构造函数
                        var twoParamCtors = serviceInfo.ServiceCtxConstructors.Where(c => c.Parameters.Count == 2).ToList();
                        if (twoParamCtors.Any())
                        {
                            // 处理第一个条件
                            var firstCtor = twoParamCtors.First();
                            generator.EnterScope($"if (parameter is {firstCtor.Parameters[1].FullTypeName})");
                            generator.AppendLine($"return new {serviceInfo.FullTypeName}(context, ({firstCtor.Parameters[1].FullTypeName})parameter);");
                            generator.LeaveScope();
                            
                            // 处理其余条件
                            for (int i = 1; i < twoParamCtors.Count; i++)
                            {
                                var ctor = twoParamCtors[i];
                                generator.AppendLine($"else if (parameter is {ctor.Parameters[1].FullTypeName})");
                                generator.EnterScope();
                                generator.AppendLine($"return new {serviceInfo.FullTypeName}(context, ({ctor.Parameters[1].FullTypeName})parameter);");
                                generator.LeaveScope();
                            }
                            
                            // 如果没有匹配的参数类型，尝试使用默认值或返回null
                            generator.AppendLine("else");
                            generator.EnterScope();
                            
                            // 尝试使用单参数构造函数（如果有的话）
                            if (singleParamCtor != null)
                            {
                                generator.AppendLine($"return new {serviceInfo.FullTypeName}(context);");
                            }
                            else
                            {
                                // 尝试使用第一个双参数构造函数，传入默认值
                                generator.AppendLine($"// No matching parameter type found");
                                generator.AppendLine($"// You may need to provide a default value for {twoParamCtors.First().Parameters[1].FullTypeName}");
                                generator.AppendLine("return null;");
                            }
                            
                            generator.LeaveScope();
                        }
                        else
                        {
                            // 有其他参数数量的构造函数，不支持
                            generator.AppendLine("// Unsupported constructor parameter count");
                            generator.AppendLine("return null;");
                        }
                    }
                }
                else
                {
                    generator.AppendLine("// No suitable constructor found");
                    generator.AppendLine("return null;");
                }
                
                generator.LeaveScope();
            }
            
            generator.AppendLine("return null;");
            generator.LeaveScope();
            
            generator.LeaveScope();
            generator.LeaveScope();
            
            context.AddSource($"IUserInterface.g.cs", generator.ToString());
        }

        private string GetFullName(ClassDeclarationSyntax syntaxNode, GeneratorExecutionContext context)
        {
            var typeSymbol = context.Compilation.GetSemanticModel(syntaxNode.SyntaxTree).GetDeclaredSymbol(syntaxNode);
            return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
        }

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new ServiceSyntaxReceiver());
        }
    }
}

