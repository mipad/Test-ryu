using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;

namespace Ryujinx.HLE.Generators
{
    internal class ServiceSyntaxReceiver : ISyntaxReceiver
    {
        public HashSet<ClassDeclarationSyntax> Types = new HashSet<ClassDeclarationSyntax>();

        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
        {
            if (syntaxNode is ClassDeclarationSyntax classDeclaration)
            {
                // 检查类是否有ServiceAttribute
                foreach (var attributeList in classDeclaration.AttributeLists)
                {
                    foreach (var attribute in attributeList.Attributes)
                    {
                        var attributeName = attribute.Name.ToString();
                        if (attributeName == "Service" || 
                            attributeName == "ServiceAttribute" ||
                            attributeName.EndsWith("Service") ||
                            attributeName.EndsWith("ServiceAttribute"))
                        {
                            Types.Add(classDeclaration);
                            return;
                        }
                    }
                }
            }
        }
    }
}

