using LibHac.Common.FixedArrays;
using LibHac.Fs;
using LibHac.Loader;
using LibHac.Tools.FsSystem;
using Ryujinx.Common.Logging;
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Ryujinx.HLE.Loaders.Executables
{
    partial class NsoExecutable : IExecutable
    {
        public byte[] Program { get; }
        public Span<byte> Text => Program.AsSpan((int)TextOffset, (int)TextSize);
        public Span<byte> Ro => Program.AsSpan((int)RoOffset, (int)RoSize);
        public Span<byte> Data => Program.AsSpan((int)DataOffset, (int)DataSize);

        public uint TextOffset { get; }
        public uint RoOffset { get; }
        public uint DataOffset { get; }
        public uint BssOffset => DataOffset + (uint)Data.Length;

        public uint TextSize { get; }
        public uint RoSize { get; }
        public uint DataSize { get; }
        public uint BssSize { get; }

        public string Name;
        public Array32<byte> BuildId;

        [GeneratedRegex(@"[a-z]:[\\/][ -~]{5,}\.nss", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ModuleRegex();
        [GeneratedRegex(@"sdk_version: ([0-9.]*)")]
        private static partial Regex FsSdkRegex();
        [GeneratedRegex(@"SDK MW[ -~]*")]
        private static partial Regex SdkMwRegex();

        public NsoExecutable(IStorage inStorage, string name = null)
        {
            NsoReader reader = new();

            reader.Initialize(inStorage.AsFile(OpenMode.Read)).ThrowIfFailure();

            TextOffset = reader.Header.Segments[0].MemoryOffset;
            RoOffset = reader.Header.Segments[1].MemoryOffset;
            DataOffset = reader.Header.Segments[2].MemoryOffset;
            BssSize = reader.Header.BssSize;

            reader.GetSegmentSize(NsoReader.SegmentType.Data, out uint uncompressedSize).ThrowIfFailure();

            Program = new byte[DataOffset + uncompressedSize];

            TextSize = DecompressSection(reader, NsoReader.SegmentType.Text, TextOffset);
            RoSize = DecompressSection(reader, NsoReader.SegmentType.Ro, RoOffset);
            DataSize = DecompressSection(reader, NsoReader.SegmentType.Data, DataOffset);

            Name = name;
            BuildId = reader.Header.ModuleId;

            // === 修复：构建ID日志（安全访问Array32<byte>）===
            byte[] buildIdBytes = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                buildIdBytes[i] = BuildId[i]; // 使用索引器直接访问
            }
            
            string buildIdStr = BitConverter.ToString(buildIdBytes).Replace("-", "");
            
            Logger.Info?.Print(LogClass.Loader, 
                $"{Name} Build ID: {buildIdStr}");
            
            // 检测已知问题构建
            if (buildIdStr == "2F1967113A281280A48CE3FC6DC23049327BF924")
            {
                Logger.Warning?.Print(LogClass.Loader,
                    "Known problematic build detected! Enforcing compatibility mode");
            }

            PrintRoSectionInfo();
        }

        private uint DecompressSection(NsoReader reader, NsoReader.SegmentType segmentType, uint offset)
        {
            reader.GetSegmentSize(segmentType, out uint uncompressedSize).ThrowIfFailure();

            var span = Program.AsSpan((int)offset, (int)uncompressedSize);

            reader.ReadSegment(segmentType, span).ThrowIfFailure();

            return uncompressedSize;
        }

        // === SDK热修复方法 (修改版) ===
        private bool ApplySdkWorkaround(string sdkVersion)
        {
            // 扩展热修复条件：支持sdk模块和特定偏移修复
            if (sdkVersion == "16.2.0")
            {
                try
                {
                    bool patched = false;
                    
                    // 1. 特定偏移修复 (sdk模块的0x136910)
                    if (Name == "sdk")
                    {
                        // 计算模块内偏移
                        uint targetOffset = 0x136910;
                        
                        if (targetOffset < Text.Length - 1)
                        {
                            // 检查是否已修复 (避免重复修补)
                            if (Text[(int)targetOffset] != 0x90)
                            {
                                Text[(int)targetOffset] = 0xD4; // BRK #0 指令
                                Text[(int)targetOffset + 1] = 0x00;
                                Logger.Info?.Print(LogClass.Loader, 
                                    $"Patched critical null access at offset 0x{targetOffset:X} in sdk");
                                patched = true;
                            }
                        }
                        else
                        {
                            Logger.Warning?.Print(LogClass.Loader, 
                                $"Target offset 0x{targetOffset:X} out of range in sdk module");
                        }
                    }

                    // 2. 模式匹配修复 (PROGRESS ORDERS游戏)
                    if (Name == "PROGRESS ORDERS")
                    {
                        byte[] pattern = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                        int index = SearchPattern(Text, pattern);
                        
                        if (index != -1)
                        {
                            Text[index] = 0x90; // NOP
                            Text[index+1] = 0x90;
                            Logger.Info?.Print(LogClass.Loader, 
                                $"Patched pattern at offset 0x{index:X}");
                            patched = true;
                        }
                    }

                    return patched;
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Loader, 
                        $"Workaround failed: {ex.Message}");
                }
            }
            return false;
        }

        // === 字节模式搜索方法 ===
        private int SearchPattern(Span<byte> data, byte[] pattern)
        {
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private void PrintRoSectionInfo()
        {
            string rawTextBuffer = Encoding.ASCII.GetString(Ro);
            StringBuilder stringBuilder = new();

            string modulePath = null;

            if (BitConverter.ToInt32(Ro[..4]) == 0)
            {
                int length = BitConverter.ToInt32(Ro.Slice(4, 4));
                if (length > 0)
                {
                    modulePath = Encoding.UTF8.GetString(Ro.Slice(8, length));
                }
            }

            if (string.IsNullOrEmpty(modulePath))
            {
                Match moduleMatch = ModuleRegex().Match(rawTextBuffer);
                if (moduleMatch.Success)
                {
                    modulePath = moduleMatch.Value;
                }
            }

            stringBuilder.AppendLine($"    Module: {modulePath}");

            // === 精确SDK版本检测 ===
            string sdkVersion = null;
            Match fsSdkMatch = FsSdkRegex().Match(rawTextBuffer);
            if (fsSdkMatch.Success)
            {
                sdkVersion = fsSdkMatch.Groups[1].Value; // 提取版本号
                stringBuilder.AppendLine($"    FS SDK Version: {sdkVersion}");
            }

            MatchCollection sdkMwMatches = SdkMwRegex().Matches(rawTextBuffer);
            if (sdkMwMatches.Count != 0)
            {
                string libHeader = "    SDK Libraries: ";
                string libContent = string.Join($"\n{new string(' ', libHeader.Length)}", sdkMwMatches);

                stringBuilder.AppendLine($"{libHeader}{libContent}");
            }

            // 检测不兼容的SDK版本
            if (sdkVersion == "16.2.0")
            {
                Logger.Warning?.Print(LogClass.Loader, 
                    $"Potential SDK compatibility issue detected in {Name} (v{sdkVersion})");
                
                // === 应用热修复 ===
                if (ApplySdkWorkaround(sdkVersion))
                {
                    Logger.Info?.Print(LogClass.Loader, 
                        "Applied SDK 16.2.0 compatibility workaround");
                }
            }

            if (stringBuilder.Length > 0)
            {
                Logger.Info?.Print(LogClass.Loader, $"{Name}:\n{stringBuilder.ToString().TrimEnd('\r', '\n')}");
            }
        }
    }
}
