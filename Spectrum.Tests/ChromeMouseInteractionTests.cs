// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace Spectrum.Tests;

// Mouse handling on the custom window chrome (drag, resize, buttons) behaves like a normal window.
public class ChromeMouseInteractionTests
{
    [Theory]
    [InlineData(MouseButtons.Right, false)]
    [InlineData(MouseButtons.Right, true)]
    [InlineData(MouseButtons.Middle, false)]
    [InlineData(MouseButtons.Middle, true)]
    [InlineData(MouseButtons.XButton1, false)]
    [InlineData(MouseButtons.XButton1, true)]
    [InlineData(MouseButtons.XButton2, false)]
    [InlineData(MouseButtons.XButton2, true)]
    [InlineData(MouseButtons.None, false)]
    [InlineData(MouseButtons.None, true)]
    public void NonLeftPressPreservesMouseEventWithoutDispatchingNativeMoveOrResize(
        MouseButtons button, bool border)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = new Form { ClientSize = new Size(400, 200) };
            using var theme = new DispatchRecordingTheme();
            form.Controls.Add(theme);
            SetPrivateField(theme, "Previous", border ? 1 : 0);
            theme.SetMouseState(Ambiance_ThemeContainer.MouseState.Over);
            var events = 0;
            theme.MouseDown += (sender, args) =>
            {
                Assert.Equal(button, args.Button);
                events++;
            };

            theme.Press(button, border ? new Point(1, 120) : new Point(100, 20));

            Assert.Equal(1, events);
            Assert.Empty(theme.NativeInteractions);
            Assert.False(IsNativeInteractionPending(theme));
            Assert.Equal(Ambiance_ThemeContainer.MouseState.Over, theme.CurrentMouseState);
        });
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 10)]
    [InlineData(2, 11)]
    [InlineData(3, 12)]
    [InlineData(4, 13)]
    [InlineData(5, 14)]
    [InlineData(6, 15)]
    [InlineData(7, 16)]
    [InlineData(8, 17)]
    public void LeftPressRetainsActualNativeHeaderAndBorderMessages(int edge, int hitTest)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = new Form { ClientSize = new Size(400, 200) };
            using var theme = new DispatchRecordingTheme();
            form.Controls.Add(theme);
            SetPrivateField(theme, "Previous", edge);
            var events = 0;
            theme.MouseDown += (sender, args) => events++;

            theme.Press(MouseButtons.Left, edge == 0 ? new Point(100, 20) : new Point(1, 120));

            var message = Assert.Single(theme.NativeInteractions);
            Assert.Equal(0x00A1, message.Msg);
            Assert.Equal(form.Handle, message.HWnd);
            Assert.Equal(new IntPtr(hitTest), message.WParam);
            Assert.Equal(1, events);
            Assert.True(IsNativeInteractionPending(theme));
            Assert.Equal(Ambiance_ThemeContainer.MouseState.Down, theme.CurrentMouseState);
        });
    }

    [Fact]
    public void LeftBorderPressStillRespectsDisabledResizing()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = new Form { ClientSize = new Size(400, 200) };
            using var theme = new DispatchRecordingTheme { Sizable = false };
            form.Controls.Add(theme);
            SetPrivateField(theme, "Previous", 1);

            theme.Press(MouseButtons.Left, new Point(1, 120));

            Assert.Empty(theme.NativeInteractions);
            Assert.False(IsNativeInteractionPending(theme));
            Assert.Equal(Ambiance_ThemeContainer.MouseState.Down, theme.CurrentMouseState);
        });
    }

    private static void SetPrivateField(Ambiance_ThemeContainer theme, string name, object value) =>
        typeof(Ambiance_ThemeContainer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(theme, value);

    private static bool IsNativeInteractionPending(Ambiance_ThemeContainer theme) =>
        (bool)typeof(Ambiance_ThemeContainer)
            .GetField("WM_LMBUTTONDOWN", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(theme);

    private sealed class DispatchRecordingTheme : Ambiance_ThemeContainer
    {
        public List<Message> NativeInteractions { get; } = new();

        public MouseState CurrentMouseState => State;

        public void SetMouseState(MouseState state) => State = state;

        public void Press(MouseButtons button, Point point) =>
            OnMouseDown(new MouseEventArgs(button, 1, point.X, point.Y, 0));

        protected override void DefWndProc(ref Message message)
        {
            if (message.Msg == 0x00A1)
            {
                NativeInteractions.Add(message);
                return;
            }
            base.DefWndProc(ref message);
        }
    }
}
