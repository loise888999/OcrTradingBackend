namespace OcrTradingBackend.Services;

public sealed record GameWindowResponse(
    long Handle,
    string ProcessName,
    string Title,
    int Left,
    int Top,
    int Width,
    int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

//public static class GameWindowResponseMapper
//{
//    public static GameWindowResponse ToResponse(GameWindowInfo window)
//    {
//        return new GameWindowResponse(
//            window.Handle.ToInt64(),
//            window.ProcessName,
//            window.Title,
//            window.Left,
//            window.Top,
//            window.Width,
//            window.Height);
//    }
//}
