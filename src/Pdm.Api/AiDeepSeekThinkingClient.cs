using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

// The SDK reads reasoning_content, but its chat message writer drops TextReasoningContent.
// Preserve the original tool exchanges privately and patch assistant messages on their way back to DeepSeek.
public sealed class AiDeepSeekThinkingClient(IChatClient inner, AiProvider provider, AiModel model, IDataProtectionProvider protection) : DelegatingChatClient(inner)
{
    private readonly IDataProtector protector = protection.CreateProtector("UPLM.AI.ThinkingContext.v1");
    private readonly string identity = provider.Id + ":" + model.Id + ":" + provider.BaseUrl.TrimEnd('/');
    private StoredMessage[]? turn;
    private sealed record StoredCall(string Id, string Name, string Arguments);
    private sealed record StoredMessage(string Role, string Text, string Reasoning, StoredCall[] Calls, string? CallId = null);
    private sealed record StoredTurn(string Identity, StoredMessage[] Messages);

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(AiDeepSeekThinkingClient) ? this : base.GetService(serviceType, serviceKey);

    public string? ProtectTurn() => turn is null ? null : protector.Protect(JsonSerializer.Serialize(new StoredTurn(identity, turn)));

    public ChatMessage[]? RestoreTurn(string? value)
    {
        if (value is null) return null;
        try {
            var stored = JsonSerializer.Deserialize<StoredTurn>(protector.Unprotect(value));
            if (stored?.Identity != identity) return null;
            return stored.Messages.Select(m => {
                var result = new ChatMessage(new ChatRole(m.Role), m.Text);
                if (m.Role == "tool") result.Contents = [new FunctionResultContent(m.CallId!, m.Text)];
                else {
                    if (m.Reasoning.Length > 0) result.Contents.Add(new TextReasoningContent(m.Reasoning));
                    foreach (var call in m.Calls) result.Contents.Add(new FunctionCallContent(call.Id, call.Name,
                        JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments)));
                }
                return result;
            }).ToArray();
        } catch (Exception ex) when (ex is CryptographicException or JsonException) { return null; }
    }

    private static ChatMessage Prepare(ChatMessage message)
    {
        if (message.Role != ChatRole.Assistant) return message;
        var raw = new OpenAI.Chat.AssistantChatMessage(message.Text);
        foreach (var call in message.Contents.OfType<FunctionCallContent>())
            raw.ToolCalls.Add(OpenAI.Chat.ChatToolCall.CreateFunctionToolCall(call.CallId, call.Name,
                BinaryData.FromString(JsonSerializer.Serialize(call.Arguments))));
        // Older non-thinking conversations have no reasoning. Explicit empty content permits mode changes.
#pragma warning disable SCME0001 // SDK JsonPatch is the supported provider-extension mechanism.
        raw.Patch.Set("$.reasoning_content"u8, JsonSerializer.SerializeToUtf8Bytes(string.Concat(message.Contents.OfType<TextReasoningContent>().Select(c => c.Text))));
#pragma warning restore SCME0001
        return new ChatMessage(message.Role, message.Contents.ToList()) { RawRepresentation = raw };
    }

    private void Capture(ChatMessage[] input, ChatResponse response)
    {
        var start = Array.FindLastIndex(input, m => m.Role == ChatRole.User);
        var messages = input.Skip(Math.Max(0, start + 1)).Concat(response.Messages);
        turn = messages.SelectMany(m => m.Role == ChatRole.Tool
            ? m.Contents.OfType<FunctionResultContent>().Select(r => new StoredMessage("tool", r.Result as string ?? JsonSerializer.Serialize(r.Result), "", [], r.CallId))
            : [new StoredMessage(m.Role.Value, m.Text, string.Concat(m.Contents.OfType<TextReasoningContent>().Select(r => r.Text)),
                m.Contents.OfType<FunctionCallContent>().Select(c => new StoredCall(c.CallId, c.Name, JsonSerializer.Serialize(c.Arguments))).ToArray())]).ToArray();
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(turn)) > model.ContextTokens * 4)
            throw new PdmRuleException("思考与查询内容超过本轮容量，请缩小查询范围或降低思考强度后重试。");
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var input = messages.ToArray();
        var response = await base.GetResponseAsync(input.Select(Prepare), options, cancellationToken);
        Capture(input, response); return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = messages.ToArray(); var updates = new List<ChatResponseUpdate>(); var size = 0;
        await foreach (var update in base.GetStreamingResponseAsync(input.Select(Prepare), options, cancellationToken)) {
            size += update.Contents.Sum(c => c switch { TextContent t => t.Text.Length, TextReasoningContent r => r.Text?.Length ?? 0, _ => 0 });
            if (size > model.ContextTokens * 4) throw new PdmRuleException("模型思考内容过长，请降低思考强度或缩小问题范围后重试。");
            updates.Add(update); yield return update;
        }
        Capture(input, updates.ToChatResponse());
    }
}
