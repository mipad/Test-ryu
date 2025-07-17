using System.Drawing;
using System.Numerics;
using Ryujinx.Common.Logging;

namespace Ryujinx.Input
{
    /// <summary>
    /// Represent an emulated mouse.
    /// </summary>
    public interface IMouse : IGamepad
    {
#pragma warning disable IDE0051 // Remove unused private member
        private const int SwitchPanelWidth = 1280;
#pragma warning restore IDE0051
        private const int SwitchPanelHeight = 720;

        /// <summary>
        /// Check if a given button is pressed on the mouse.
        /// </summary>
        /// <param name="button">The button</param>
        /// <returns>True if the given button is pressed on the mouse</returns>
        bool IsButtonPressed(MouseButton button);

        /// <summary>
        /// Get the position of the mouse in the client.
        /// </summary>
        Vector2 GetPosition();

        /// <summary>
        /// Get the mouse scroll delta.
        /// </summary>
        Vector2 GetScroll();

        /// <summary>
        /// Get the client size.
        /// </summary>
        Size ClientSize { get; }

        /// <summary>
        /// Get the button states of the mouse.
        /// </summary>
        bool[] Buttons { get; }

        /// <summary>
        /// Get a snaphost of the state of a mouse.
        /// </summary>
        /// <param name="mouse">The mouse to do a snapshot of</param>
        /// <returns>A snaphost of the state of the mouse.</returns>
        public static MouseStateSnapshot GetMouseStateSnapshot(IMouse mouse)
        {
            bool[] buttons = new bool[(int)MouseButton.Count];

            mouse.Buttons.CopyTo(buttons, 0);

            return new MouseStateSnapshot(buttons, mouse.GetPosition(), mouse.GetScroll());
        }

        /// <summary>
        /// Get the position of a mouse on screen relative to the app's view
        /// </summary>
        /// <param name="mousePosition">The position of the mouse in the client</param>
        /// <param name="clientSize">The size of the client</param>
        /// <param name="aspectRatio">The aspect ratio of the view</param>
        /// <returns>A snaphost of the state of the mouse.</returns>
        public static Vector2 GetScreenPosition(Vector2 mousePosition, Size clientSize, float aspectRatio)
{
    float mouseX = mousePosition.X;
    float mouseY = mousePosition.Y;

    float aspectWidth = SwitchPanelHeight * aspectRatio;

    int screenWidth = clientSize.Width;
    int screenHeight = clientSize.Height;

    if (clientSize.Width > clientSize.Height * aspectWidth / SwitchPanelHeight)
    {
        screenWidth = (int)(clientSize.Height * aspectWidth) / SwitchPanelHeight;
    }
    else
    {
        screenHeight = (clientSize.Width * SwitchPanelHeight) / (int)aspectWidth;
    }

    int startX = (clientSize.Width - screenWidth) >> 1;
    int startY = (clientSize.Height - screenHeight) >> 1;

    int endX = startX + screenWidth;
    int endY = startY + screenHeight;

    // 添加详细的调试日志
    #if DEBUG
    string logMessage = $"[Touch] Input: ({mousePosition.X:F0}, {mousePosition.Y:F0}) | " +
                        $"Client: {clientSize.Width}x{clientSize.Height} | " +
                        $"AspectRatio: {aspectRatio:F2} | " +
                        $"GameArea: {screenWidth}x{screenHeight} | " +
                        $"Bounds: ({startX},{startY})-({endX},{endY})";
    
    Ryujinx.Common.Logging.Logger.Debug?.PrintMsg(Ryujinx.Common.Logging.LogClass.Application, logMessage);
    #endif

    if (mouseX >= startX &&
        mouseY >= startY &&
        mouseX < endX &&
        mouseY < endY)
    {
        int screenMouseX = (int)mouseX - startX;
        int screenMouseY = (int)mouseY - startY;

        mouseX = (screenMouseX * (int)aspectWidth) / screenWidth;
        mouseY = (screenMouseY * SwitchPanelHeight) / screenHeight;

        // 添加转换后坐标的日志
        #if DEBUG
        Logger.Debug?.PrintMsg(LogClass.Application, 
            $"[Touch] Converted: ({mouseX:F0}, {mouseY:F0})");
        #endif

        return new Vector2(mouseX, mouseY);
    }

    // 添加无效触摸的日志
    #if DEBUG
    Logger.Debug?.PrintMsg(LogClass.Application, 
        $"[Touch] Outside game area - ignored");
    #endif

    return new Vector2();
}
    }
}
