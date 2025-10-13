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

            // 直接使用 Android 标准路径
            string hostsFilePath = AndroidHostsFilePath;

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

                        if (entry.Length < 2)
                        {
                            continue;
                        }

                        if (!IPAddress.TryParse(entry[0], out IPAddress address))
                        {
                            continue;
                        }

                        // 处理 AMS hosts 文件扩展
                        for (int i = 1; i < entry.Length; i++)
                        {
                            entry[i] = entry[i].Replace("%", IManager.NsdSettings.Environment);
                        }

                        foreach (string hostname in entry[1..])
                        {
                            _mitmHostEntries[hostname] = address;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning?.PrintMsg(LogClass.ServiceBsd, $"Failed to load hosts file: {ex.Message}");
                }
            }
        }

        public IPHostEntry ResolveAddress(string host)
        {
            foreach (var hostEntry in _mitmHostEntries)
            {
                if (FileSystemName.MatchesSimpleExpression(hostEntry.Key, host))
                {
                    return new IPHostEntry
                    {
                        AddressList = new[] { hostEntry.Value },
                        HostName = hostEntry.Key,
                        Aliases = Array.Empty<string>(),
                    };
                }
            }

            // 使用系统 DNS
            try
            {
                return Dns.GetHostEntry(host);
            }
            catch (Exception)
            {
                // 返回空的 IPHostEntry
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
