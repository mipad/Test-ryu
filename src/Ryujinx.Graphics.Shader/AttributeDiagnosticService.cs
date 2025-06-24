using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using Ryujinx.Graphics.Shader; // 添加缺失的命名空间引用
using Ryujinx.Graphics.Shader.Translation;

public class AttributeDiagnosticService
{
    private static AttributeDiagnosticService _instance;
    public static AttributeDiagnosticService Instance => 
        _instance ??= new AttributeDiagnosticService();
    
    private readonly ConcurrentDictionary<int, (AttributeType, AggregateType)> _invalidAttributes = new();
    
    // 修复可访问性问题：将方法改为 internal
    internal void RecordInvalidAttribute(int location, AttributeType originalType, AggregateType resolvedType)
    {
        _invalidAttributes.TryAdd(location, (originalType, resolvedType));
    }
    
    public string GenerateDiagnosticReport()
    {
        if (_invalidAttributes.IsEmpty) return "No invalid attributes detected";
        
        var report = new StringBuilder();
        report.AppendLine("Shader Attribute Diagnostics Report");
        report.AppendLine("===================================");
        report.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();
        
        report.AppendLine("Invalid Attribute Locations:");
        report.AppendLine("Location | Original Type | Resolved Type");
        report.AppendLine("-------- | ------------- | -------------");
        
        foreach (var entry in _invalidAttributes.OrderBy(e => e.Key))
        {
            report.AppendLine($"{entry.Key,8} | {entry.Value.Item1,-13} | {entry.Value.Item2}");
        }
        
        return report.ToString();
    }
    
    public void Reset() => _invalidAttributes.Clear();
}
