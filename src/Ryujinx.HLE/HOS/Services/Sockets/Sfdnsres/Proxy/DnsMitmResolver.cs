using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Sockets.Nsd;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Net;

namespace Ryujinx.HLE.HOS.Services.Sockets.Sfdnsres.Proxy
{
    class DnsMitmResolver
    {
        // Android 系统的标准 hosts 文件路径
        private const string AndroidHostsFilePath = "/system/etc/hosts";

        private static DnsMitmResolver _instance;
        public static DnsMitmResolver Instance => _instance ??= new DnsMitmResolver();

        private readonly Dictionary<string, IPAddress> _mitmHostEntries = new();

        public void ReloadEntries(ServiceCtx context)
        {
            _mitmHostEntries.Clear();

            string hostsFilePath = FindHostsFile();
            
            if (string.IsNullOrEmpty(hostsFilePath))
            {
                Logger.Info?.PrintMsg(LogClass.ServiceBsd, "No hosts file found, DNS MITM will use system DNS");
                return;
            }

            if (File.Exists(hostsFilePath))
            {
                try
                {
                    using FileStream fileStream = File.Open(hostsFilePath, FileMode.Open, FileAccess.Read);
                    using StreamReader reader = new(fileStream);

                    while (!reader.EndOfStream)
                    {
                        string line = reader.ReadLine();

                        if (line == null)
                        {
                            break;
                        }

                        // Ignore comments and empty lines
                        if (line.StartsWith('#') || line.Trim().Length == 0)
                        {
                            continue;
                        }

                        string[] entry = line.Split(new[] { ' ', '\t' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                        // Hosts file example entry:
                        // 127.0.0.1  localhost loopback

                        // 0. Check the size of the array
                        if (entry.Length < 2)
                        {
                            Logger.Warning?.PrintMsg(LogClass.ServiceBsd, $"Invalid entry in hosts file: {line}");
                            continue;
                        }

                        // 1. Parse the address
                        if (!IPAddress.TryParse(entry[0], out IPAddress address))
                        {
                            Logger.Warning?.PrintMsg(LogClass.ServiceBsd, $"Failed to parse IP address in hosts file: {entry[0]}");
                            continue;
                        }

                        // 2. Check for AMS hosts file extension: "%"
                        for (int i = 1; i < entry.Length; i++)
                        {
                            entry[i] = entry[i].Replace("%", IManager.NsdSettings.Environment);
                        }

                        // 3. Add hostname to entry dictionary (updating duplicate entries)
                        foreach (string hostname in entry[1..])
                        {
                            _mitmHostEntries[hostname] = address;
                        }
                    }

                    Logger.Info?.PrintMsg(LogClass.ServiceBsd, $"Loaded {_mitmHostEntries.Count} DNS MITM entries from: {hostsFilePath}");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.PrintMsg(LogClass.ServiceBsd, $"Failed to load hosts file {hostsFilePath}: {ex.Message}");
                }
            }
            else
            {
                Logger.Info?.PrintMsg(LogClass.ServiceBsd, $"Hosts file not found: {hostsFilePath}");
            }
        }

        /// <summary>
        /// 查找可用的 hosts 文件
        /// </summary>
        private string FindHostsFile()
        {
            // Android 系统使用标准的 hosts 文件路径
            if (OperatingSystem.IsAndroid())
            {
                return AndroidHostsFilePath;
            }
            else
            {
                // 非 Android 平台使用原有逻辑
                string sdPath = FileSystem.VirtualFileSystem.GetSdCardPath();
                return FileSystem.VirtualFileSystem.GetFullPath(sdPath, "/atmosphere/hosts/default.txt");
            }
        }

        public IPHostEntry ResolveAddress(string host)
        {
            // 首先检查 MITM 条目
            foreach (var hostEntry in _mitmHostEntries)
            {
                // Check for AMS hosts file extension: "*"
                // NOTE: MatchesSimpleExpression also allows "?" as a wildcard
                if (FileSystemName.MatchesSimpleExpression(hostEntry.Key, host))
                {
                    Logger.Info?.PrintMsg(LogClass.ServiceBsd, $"Redirecting '{host}' to: {hostEntry.Value}");

                    return new IPHostEntry
                    {
                        AddressList = new[] { hostEntry.Value },
                        HostName = hostEntry.Key,
                        Aliases = Array.Empty<string>(),
                    };
                }
            }

            // No match has been found, resolve the host using regular DNS
            try
            {
                return Dns.GetHostEntry(host);
            }
            catch (Exception ex)
            {
                Logger.Warning?.PrintMsg(LogClass.ServiceBsd, $"DNS resolution failed for '{host}': {ex.Message}");
                
                // 返回一个空的 IPHostEntry 而不是抛出异常
                return new IPHostEntry
                {
                    AddressList = Array.Empty<IPAddress>(),
                    HostName = host,
                    Aliases = Array.Empty<string>(),
                };
            }
        }
    }
}
