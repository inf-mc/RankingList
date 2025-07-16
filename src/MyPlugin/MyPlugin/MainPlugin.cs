using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serein.Core.Models.Abstractions;
using Serein.Core.Models.Network.Connection;
using Serein.Core.Services.Network.Connection;
using Serein.Core.Services.Plugins.Net;

namespace MyPlugin;

public class MainPlugin : PluginBase
{
    private readonly PluginLoggerBase _logger;
    private readonly ConnectionManager _connectionManager;

    public MainPlugin(IServiceProvider serviceProvider, ConnectionManager connectionManager)
    {
        _logger = serviceProvider.GetRequiredService<PluginLoggerBase>();
        _connectionManager = connectionManager;
    }

    public override void Dispose()
    {
        // Clean up resources if needed
    }

    protected override async Task<bool> OnGroupMessageReceived(Packets packet)
    {
        var root = DeserializePacket(packet);

        if (root?.OneBotV11?.Message?.Count > 0)
        {
            string text = root.OneBotV11.Message[0].Data.Text;

            var setting = LoadSettings();

            var command = setting.RankCommands.FirstOrDefault(c =>
                c.Keyword == text || (!string.IsNullOrEmpty(c.KeywordAll) && c.KeywordAll == text));

            if (command == null)
            {
                return true;
            }

            bool isGetAll = command.KeywordAll == text;

            string body = await GenerateRankingAsync(command, setting, isGetAll);

            long? groupId = root.OneBotV11.GroupId;

            if (!groupId.HasValue)
            {
                return true;
            }

            var nodes = new[]
                {
                    new
                    {
                        type = "node",
                        data = new
                        {
                            user_id = 2590759258,
                            nickname = "音符排行榜",
                            content = body
                        }
                    }
                };
            var forwardMessage = new
            {
                message_type = "group",
                group_id = groupId,
                messages = nodes
            };
            var request = new
            {
                action = "send_forward_msg",
                @params = forwardMessage
            };
            string json = JsonSerializer.Serialize(request);
            await _connectionManager.SendDataAsync(json);

            //await _connectionManager.SendMessageAsync(
            //    TargetType.Group,
            //    groupId.ToString(),
            //    body
            //);

            return true;
        }

        return true;
    }

    private Root? DeserializePacket(Packets packet)
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            string json = JsonSerializer.Serialize(packet, new JsonSerializerOptions { WriteIndented = true });
            return JsonSerializer.Deserialize<Root>(json, options);
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, "Deserialize", $"反序列化数据失败: {ex.Message}");
            return null;
        }
    }

    private PluginSetting LoadSettings()
    {
        string pluginDir = Path.Combine(Directory.GetCurrentDirectory(), "Serein" , "plugins", "RankingList");
        string settingPath = Path.Combine(pluginDir, "setting.json");

        if (!File.Exists(settingPath))
        {
            _logger.Log(LogLevel.Error, "Settings", $"配置文件不存在: {settingPath}");
            throw new FileNotFoundException("插件配置文件不存在");
        }

        string json = File.ReadAllText(settingPath);
        return JsonSerializer.Deserialize<PluginSetting>(json) ?? throw new InvalidOperationException("解析配置失败");
    }

    private async Task<string> GenerateRankingAsync(RankCommandConfig command, PluginSetting setting, bool isGetAll)
    {
        var mapping = setting.StatsMapping.GetValueOrDefault(command.Type);
        if (mapping == null)
        {
            return "未找到对应的统计类型";
        }

        string usercachePath = Path.Combine(Directory.GetCurrentDirectory(), setting.FilePaths.UserCache);
        string statsDirectory = Path.Combine(Directory.GetCurrentDirectory(), setting.FilePaths.StatsDirectory);

        if (!File.Exists(usercachePath))
        {
            _logger.Log(LogLevel.Warning, "Settings", "usercache.json 不存在");
            return "无法加载玩家数据";
        }

        string usercacheJson = File.ReadAllText(usercachePath);
        var entries = JsonSerializer.Deserialize<List<UserCacheEntry>>(usercacheJson) ?? new List<UserCacheEntry>();

        var rankingList = new List<RankItem>();

        foreach (var entry in entries)
        {
            string filePath = Path.Combine(statsDirectory, $"{entry.uuid}.json");

            if (!File.Exists(filePath))
            {
                continue;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);

                JsonElement current = doc.RootElement;

                bool success = true;
                foreach (var key in mapping.Path)
                {
                    success &= current.TryGetProperty(key, out current);
                }

                if (!success)
                {
                    continue;
                }

                long value = 0;

                if (current.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in current.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Number)
                        {
                            value += property.Value.GetInt64();
                        }
                    }
                }
                else if (current.ValueKind == JsonValueKind.Number)
                {
                    value = current.GetInt64();
                }

                if (mapping.Transform == "ss")
                {
                    value /= 20;
                }
                if (mapping.Transform == "mm")
                {
                    value /= 1200;
                }
                if (mapping.Transform == "hh")
                {
                    value /= 72000;
                }
                if (mapping.Transform == "dd")
                {
                    value /= 1728000;
                }
                if (mapping.Transform == "k")
                {
                    value /= 1000;
                }
                if (mapping.Transform == "w")
                {
                    value /= 10000;
                }
                if (mapping.Transform == "m")
                {
                    value /= 1000000;
                }

                if (value > 0)
                {
                    rankingList.Add(new RankItem
                    {
                        Name = entry.name,
                        Value = value
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, "Ranking", $"读取 {entry.uuid} 失败: {ex.Message}");
            }
        }

        // 排序逻辑
        if (command.SortOrder.Equals("asc", StringComparison.OrdinalIgnoreCase))
        {
            rankingList = rankingList.OrderBy(r => r.Value).ToList();
        }
        else
        {
            rankingList = rankingList.OrderByDescending(r => r.Value).ToList();
        }

        // 控制输出数量
        if (!isGetAll)
        {
            rankingList = rankingList.Take(command.TopN).ToList();
        }

        var result = new StringBuilder();
        result.AppendLine(command.Title);

        foreach (var item in rankingList.Select((value, i) => new { Index = i, Value = value }))
        {
            result.AppendLine(command.Format
                .Replace("{index}", (item.Index + 1).ToString())
                .Replace("{name}", item.Value.Name)
                .Replace("{value}", item.Value.Value.ToString()));
        }

        return result.ToString().TrimEnd('\n');
    }
}

// ====== 配置模型类 ======

public class PluginSetting
{
    [JsonPropertyName("RankCommands")]
    public List<RankCommandConfig> RankCommands { get; set; } = [];

    [JsonPropertyName("StatsMapping")]
    public Dictionary<string, StatsMappingConfig> StatsMapping { get; set; } = [];

    [JsonPropertyName("FilePaths")]
    public FilePathsConfig FilePaths { get; set; } = new();
}

public class RankCommandConfig
{
    [JsonPropertyName("Keyword")]
    public string Keyword { get; set; } = "";

    [JsonPropertyName("KeywordAll")]
    public string KeywordAll { get; set; } = "";

    [JsonPropertyName("Type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("Title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("Format")]
    public string Format { get; set; } = "";

    [JsonPropertyName("SortOrder")]
    public string SortOrder { get; set; } = "desc";

    [JsonPropertyName("TopN")]
    public int TopN { get; set; } = 10;
}

public class StatsMappingConfig
{
    [JsonPropertyName("Path")]
    public List<string> Path { get; set; } = [];

    [JsonPropertyName("Transform")]
    public string? Transform { get; set; } = "";
}

public class FilePathsConfig
{
    [JsonPropertyName("UserCache")]
    public string UserCache { get; set; } = "";

    [JsonPropertyName("StatsDirectory")]
    public string StatsDirectory { get; set; } = "";
}

// ====== 数据模型类 ======

public class UserCacheEntry
{
    public string name { get; set; } = "";
    public string uuid { get; set; } = "";
}

public class RankItem
{
    public string Name { get; set; } = "";
    public long Value { get; set; }
}

// ====== Packet Models ======

public class Root
{
    public OneBotV11? OneBotV11 { get; set; }
}

public class OneBotV11
{
    public List<Message>? Message { get; set; }
    public Sender? Sender { get; set; }
    public long GroupId { get; set; }
}

public class Message
{
    public string? Type { get; set; }
    public MessageData? Data { get; set; }
}

public class MessageData
{
    public string? Text { get; set; }
}

public class Sender
{
    public long UserId { get; set; }
}
