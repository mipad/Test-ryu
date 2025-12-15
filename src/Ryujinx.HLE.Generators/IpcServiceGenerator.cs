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
            public bool HasServiceCtxConstructor { get; set; }
            public bool HasServiceCtxAndParameterConstructor { get; set; }
            public string ParameterType { get; set; }
            public string ParameterTypeFullName { get; set; }
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
                var serviceAttribute = classDeclaration.AttributeLists
                    .SelectMany(x => x.Attributes)
                    .FirstOrDefault(y => 
                        y.Name.ToString() == "Service" || 
                        y.Name.ToString() == "ServiceAttribute" ||
                        y.Name.ToString().EndsWith("Service") ||
                        y.Name.ToString().EndsWith("ServiceAttribute"));
                    
                if (serviceAttribute == null)
                {
                    continue;
                }

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
                    else if (firstArg.Expression is InvocationExpressionSyntax invocation &&
                             invocation.Expression.ToString() == "nameof")
                    {
                        // 处理 nameof(...) 的情况
                        serviceName = invocation.ArgumentList.Arguments[0].ToString();
                    }
                }

                if (string.IsNullOrEmpty(serviceName))
                {
                    continue;
                }

                // 获取完整类型名
                var fullTypeName = GetFullName(classDeclaration, context);
                
                // 检查是否在服务命名空间内
                if (!fullTypeName.StartsWith("Ryujinx.HLE.HOS.Services."))
                {
                    continue;
                }

                // 检查构造函数
                var constructors = classDeclaration.ChildNodes()
                    .OfType<ConstructorDeclarationSyntax>()
                    .ToList();

                bool hasServiceCtxConstructor = false;
                bool hasServiceCtxAndParameterConstructor = false;
                string parameterType = null;
                string parameterTypeFullName = null;

                foreach (var constructor in constructors)
                {
                    var parameters = constructor.ParameterList.Parameters;
                    
                    if (parameters.Count >= 1)
                    {
                        var firstParamType = parameters[0].Type.ToString();
                        if (firstParamType == "ServiceCtx" || 
                            firstParamType.EndsWith(".ServiceCtx") ||
                            firstParamType == "Ryujinx.HLE.HOS.Ipc.ServiceCtx")
                        {
                            hasServiceCtxConstructor = true;
                            
                            if (parameters.Count == 2)
                            {
                                hasServiceCtxAndParameterConstructor = true;
                                parameterType = parameters[1].Type.ToString();
                                
                                // 获取完整的参数类型名
                                var paramTypeSymbol = context.Compilation.GetSemanticModel(constructor.SyntaxTree)
                                    .GetSymbolInfo(parameters[1].Type).Symbol as INamedTypeSymbol;
                                
                                if (paramTypeSymbol != null)
                                {
                                    parameterTypeFullName = paramTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                                }
                                else
                                {
                                    // 如果无法获取符号，使用原始字符串
                                    parameterTypeFullName = parameterType;
                                }
                            }
                        }
                    }
                }

                if (!hasServiceCtxConstructor)
                {
                    continue;
                }

                serviceInfos.Add(new ServiceInfo
                {
                    FullTypeName = fullTypeName,
                    ServiceName = serviceName,
                    HasServiceCtxConstructor = hasServiceCtxConstructor,
                    HasServiceCtxAndParameterConstructor = hasServiceCtxAndParameterConstructor,
                    ParameterType = parameterType,
                    ParameterTypeFullName = parameterTypeFullName
                });
            }

            // 生成代码
            CodeGenerator generator = new CodeGenerator();
            
            // 添加必要的using指令
            generator.AppendLine("using System;");
            generator.AppendLine("using System.Collections.Generic;");
            
            generator.EnterScope($"namespace Ryujinx.HLE.HOS.Services.Sm");
            generator.EnterScope($"partial class IUserInterface");
            
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
            
            foreach (var serviceInfo in serviceInfos)
            {
                generator.EnterScope($"if (type == typeof({serviceInfo.FullTypeName}))");
                
                if (serviceInfo.HasServiceCtxAndParameterConstructor && 
                    !string.IsNullOrEmpty(serviceInfo.ParameterTypeFullName))
                {
                    generator.EnterScope($"if (parameter != null)");
                    generator.AppendLine($"return new {serviceInfo.FullTypeName}(context, ({serviceInfo.ParameterTypeFullName})parameter);");
                    generator.LeaveScope();
                    generator.AppendLine($"else");
                    generator.IncreaseIndentation();
                    generator.AppendLine($"return new {serviceInfo.FullTypeName}(context);");
                    generator.DecreaseIndentation();
                }
                else
                {
                    generator.AppendLine($"return new {serviceInfo.FullTypeName}(context);");
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
