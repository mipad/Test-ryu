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
                var serviceAttribute = classDeclaration.AttributeLists
                    .SelectMany(x => x.Attributes)
                    .FirstOrDefault(y => 
                        y.Name.ToString() == "Service" || 
                        y.Name.ToString() == "ServiceAttribute");
                    
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

                // 获取所有构造函数信息
                var constructors = classDeclaration.ChildNodes()
                    .OfType<ConstructorDeclarationSyntax>()
                    .ToList();

                var serviceInfo = new ServiceInfo
                {
                    FullTypeName = fullTypeName,
                    ServiceName = serviceName
                };

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
                    
                    serviceInfo.Constructors.Add(ctorInfo);
                }

                serviceInfos.Add(serviceInfo);
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
            
            foreach (var serviceInfo in serviceInfos)
            {
                generator.EnterScope($"if (type == typeof({serviceInfo.FullTypeName}))");
                
                // 查找最适合的构造函数
                var suitableConstructors = serviceInfo.Constructors
                    .Where(c => c.Parameters.Count >= 1 && 
                           (c.Parameters[0].FullTypeName.Contains("ServiceCtx") || 
                            c.Parameters[0].FullTypeName.EndsWith("ServiceCtx")))
                    .ToList();
                
                if (suitableConstructors.Any())
                {
                    // 首先尝试匹配参数数量的构造函数
                    var constructor = suitableConstructors.FirstOrDefault(c => c.Parameters.Count == 1);
                    
                    if (constructor != null)
                    {
                        // 只有一个ServiceCtx参数的构造函数
                        generator.AppendLine($"return new {serviceInfo.FullTypeName}(context);");
                    }
                    else if (parameter != null)
                    {
                        // 尝试找到匹配参数的构造函数
                        foreach (var ctor in suitableConstructors.Where(c => c.Parameters.Count == 2))
                        {
                            generator.AppendLine($"if (parameter is {ctor.Parameters[1].FullTypeName})");
                            generator.IncreaseIndentation();
                            generator.AppendLine($"return new {serviceInfo.FullTypeName}(context, ({ctor.Parameters[1].FullTypeName})parameter);");
                            generator.DecreaseIndentation();
                        }
                    }
                    else
                    {
                        // 没有参数，但有需要参数的构造函数
                        generator.AppendLine("// This service requires a parameter, but none was provided");
                        generator.AppendLine("// You may need to check the ServiceAttribute for this service");
                        generator.AppendLine("return null;");
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

