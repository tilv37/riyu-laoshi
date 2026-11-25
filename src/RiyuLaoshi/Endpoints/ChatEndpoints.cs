using RiyuLaoshi.Services;

namespace RiyuLaoshi.Endpoints;

// 聊天接口：两者都以 SSE 返回，流式内容与结束/错误事件走同一通道
public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        app.MapPost("/api/chat", (HttpContext context, ChatRequest request, ChatService chat) =>
            chat.HandleAsync(context, request, regenerate: false));

        app.MapPost("/api/chat/regenerate", (HttpContext context, RegenerateRequest request, ChatService chat) =>
            chat.HandleAsync(
                context,
                new ChatRequest(request.SessionId, "", request.ContextRounds, request.IncludeContext),
                regenerate: true));
    }
}
