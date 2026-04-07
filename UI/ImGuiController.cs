using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PhotoOrganizer.UI;

/// <summary>
/// OpenGL/OpenTK backend for ImGui.NET rendering.
/// Input is read from Win32GameWindow's tracked input state.
/// </summary>
public class ImGuiController : IDisposable
{
    private int _vertexArray;
    private int _vertexBuffer;
    private int _vertexBufferSize;
    private int _indexBuffer;
    private int _indexBufferSize;
    private int _fontTexture;
    private int _shaderProgram;
    private int _uniformProjection;

    private int _windowWidth;
    private int _windowHeight;

    private bool _frameBegun;

    public ImGuiController(int width, int height)
    {
        _windowWidth = width;
        _windowHeight = height;

        IntPtr ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(ctx);

        var io = ImGui.GetIO();
        io.Fonts.AddFontDefault();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        CreateDeviceResources();
        SetKeyMappings();

        SetPerFrameData(1f / 60f);
        ImGui.NewFrame();
        _frameBegun = true;
    }

    private void SetPerFrameData(float deltaSeconds)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new System.Numerics.Vector2(_windowWidth, _windowHeight);
        io.DisplayFramebufferScale = System.Numerics.Vector2.One;
        io.DeltaTime = deltaSeconds;
    }

    private void CreateDeviceResources()
    {
        _vertexBufferSize = 10000;
        _indexBufferSize = 2000;

        _vertexArray = GL.GenVertexArray();
        GL.BindVertexArray(_vertexArray);

        _vertexBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        _indexBuffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        RecreateFontDeviceTexture();

        const string vertSource = @"#version 330 core
uniform mat4 projection_matrix;
layout(location = 0) in vec2 in_position;
layout(location = 1) in vec2 in_texCoord;
layout(location = 2) in vec4 in_color;
out vec2 frag_texCoord;
out vec4 frag_color;
void main() {
    gl_Position = projection_matrix * vec4(in_position, 0, 1);
    frag_texCoord = in_texCoord;
    frag_color = in_color;
}";

        const string fragSource = @"#version 330 core
uniform sampler2D in_fontTexture;
in vec2 frag_texCoord;
in vec4 frag_color;
out vec4 out_color;
void main() {
    out_color = frag_color * texture(in_fontTexture, frag_texCoord);
}";

        _shaderProgram = CompileShader(vertSource, fragSource);
        _uniformProjection = GL.GetUniformLocation(_shaderProgram, "projection_matrix");
        int uniformFontTexture = GL.GetUniformLocation(_shaderProgram, "in_fontTexture");

        GL.UseProgram(_shaderProgram);
        GL.Uniform1(uniformFontTexture, 0);

        // Set up vertex attributes
        GL.BindVertexArray(_vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);

        int stride = Unsafe.SizeOf<ImDrawVert>();
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    private void RecreateFontDeviceTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int _);

        _fontTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        io.Fonts.SetTexID((IntPtr)_fontTexture);
        io.Fonts.ClearTexData();
    }

    private static int CompileShader(string vertSrc, string fragSrc)
    {
        int vert = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vert, vertSrc);
        GL.CompileShader(vert);
        GL.GetShader(vert, ShaderParameter.CompileStatus, out int vertStatus);
        if (vertStatus == 0)
            throw new Exception("Vertex shader compile error: " + GL.GetShaderInfoLog(vert));

        int frag = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(frag, fragSrc);
        GL.CompileShader(frag);
        GL.GetShader(frag, ShaderParameter.CompileStatus, out int fragStatus);
        if (fragStatus == 0)
            throw new Exception("Fragment shader compile error: " + GL.GetShaderInfoLog(frag));

        int prog = GL.CreateProgram();
        GL.AttachShader(prog, vert);
        GL.AttachShader(prog, frag);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
            throw new Exception("Shader link error: " + GL.GetProgramInfoLog(prog));

        GL.DetachShader(prog, vert);
        GL.DetachShader(prog, frag);
        GL.DeleteShader(vert);
        GL.DeleteShader(frag);

        return prog;
    }

    public void Update(Win32GameWindow wnd, float deltaSeconds)
    {
        if (_frameBegun) ImGui.Render();

        SetPerFrameData(deltaSeconds);
        UpdateInput(wnd);
        _frameBegun = true;
        ImGui.NewFrame();
    }

    private void UpdateInput(Win32GameWindow wnd)
    {
        var io = ImGui.GetIO();

        io.MousePos = new System.Numerics.Vector2(wnd.MouseX, wnd.MouseY);
        io.MouseDown[0] = wnd.MouseButtons[0];
        io.MouseDown[1] = wnd.MouseButtons[1];
        io.MouseDown[2] = wnd.MouseButtons[2];
        io.MouseWheel = wnd.ScrollDelta;

        // Drain pending characters
        while (wnd.PendingChars.Count > 0)
            io.AddInputCharacter(wnd.PendingChars.Dequeue());

        // Map VK codes to ImGuiKey
        for (int vk = 0; vk < 256; vk++)
        {
            ImGuiKey imKey = TranslateVK(vk);
            if (imKey != ImGuiKey.None)
                io.AddKeyEvent(imKey, wnd.IsKeyDown(vk));
        }

        // Modifier keys
        io.AddKeyEvent(ImGuiKey.ModCtrl,  wnd.IsKeyDown(162) || wnd.IsKeyDown(163)); // VK_LCONTROL, VK_RCONTROL
        io.AddKeyEvent(ImGuiKey.ModShift, wnd.IsKeyDown(160) || wnd.IsKeyDown(161)); // VK_LSHIFT, VK_RSHIFT
        io.AddKeyEvent(ImGuiKey.ModAlt,   wnd.IsKeyDown(164) || wnd.IsKeyDown(165)); // VK_LMENU, VK_RMENU
    }

    public void PressChar(char c)
    {
        ImGui.GetIO().AddInputCharacter(c);
    }

    public void MouseScroll(Vector2 offset)
    {
        ImGui.GetIO().MouseWheel = offset.Y;
    }

    public void WindowResized(int width, int height)
    {
        _windowWidth = width;
        _windowHeight = height;
    }

    public void Render()
    {
        if (_frameBegun)
        {
            _frameBegun = false;
            ImGui.Render();
            RenderDrawData(ImGui.GetDrawData());
        }
    }

    private void RenderDrawData(ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount == 0) return;

        int fbWidth = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
        int fbHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (fbWidth == 0 || fbHeight == 0) return;

        // Backup GL state
        GL.GetInteger(GetPName.ActiveTexture, out int prevActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.CurrentProgram, out int prevProgram);
        GL.GetInteger(GetPName.TextureBinding2D, out int prevTexture);
        GL.GetInteger(GetPName.ArrayBufferBinding, out int prevArrayBuffer);
        GL.GetInteger(GetPName.VertexArrayBinding, out int prevVertexArray);
        Span<int> prevScissorBox = stackalloc int[4];
        unsafe { fixed (int* p = prevScissorBox) GL.GetInteger(GetPName.ScissorBox, p); }

        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha,
                             BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.StencilTest);
        GL.Enable(EnableCap.ScissorTest);

        GL.Viewport(0, 0, fbWidth, fbHeight);

        float L = drawData.DisplayPos.X;
        float R = drawData.DisplayPos.X + drawData.DisplaySize.X;
        float T = drawData.DisplayPos.Y;
        float B = drawData.DisplayPos.Y + drawData.DisplaySize.Y;

        var projection = new Matrix4(
            2.0f / (R - L), 0, 0, 0,
            0, 2.0f / (T - B), 0, 0,
            0, 0, -1, 0,
            (R + L) / (L - R), (T + B) / (B - T), 0, 1
        );

        GL.UseProgram(_shaderProgram);
        GL.UniformMatrix4(_uniformProjection, false, ref projection);

        GL.BindVertexArray(_vertexArray);

        var clipOff = drawData.DisplayPos;
        var clipScale = drawData.FramebufferScale;

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];

            int vtxSize = cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>();
            int idxSize = cmdList.IdxBuffer.Size * sizeof(ushort);

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            if (vtxSize > _vertexBufferSize)
            {
                _vertexBufferSize = (int)(vtxSize * 1.5f);
                GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vtxSize, cmdList.VtxBuffer.Data);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            if (idxSize > _indexBufferSize)
            {
                _indexBufferSize = (int)(idxSize * 1.5f);
                GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, idxSize, cmdList.IdxBuffer.Data);

            for (int cmdIdx = 0; cmdIdx < cmdList.CmdBuffer.Size; cmdIdx++)
            {
                var cmd = cmdList.CmdBuffer[cmdIdx];
                if (cmd.UserCallback != IntPtr.Zero) continue;

                var clipMin = new System.Numerics.Vector2(
                    (cmd.ClipRect.X - clipOff.X) * clipScale.X,
                    (cmd.ClipRect.Y - clipOff.Y) * clipScale.Y);
                var clipMax = new System.Numerics.Vector2(
                    (cmd.ClipRect.Z - clipOff.X) * clipScale.X,
                    (cmd.ClipRect.W - clipOff.Y) * clipScale.Y);

                if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y) continue;

                GL.Scissor((int)clipMin.X, fbHeight - (int)clipMax.Y,
                           (int)(clipMax.X - clipMin.X), (int)(clipMax.Y - clipMin.Y));

                GL.BindTexture(TextureTarget.Texture2D, (int)cmd.TextureId);
                GL.DrawElementsBaseVertex(PrimitiveType.Triangles, (int)cmd.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (IntPtr)(cmd.IdxOffset * sizeof(ushort)),
                    (int)cmd.VtxOffset);
            }
        }

        // Restore GL state
        GL.UseProgram(prevProgram);
        GL.BindTexture(TextureTarget.Texture2D, prevTexture);
        GL.ActiveTexture((TextureUnit)prevActiveTexture);
        GL.BindVertexArray(prevVertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, prevArrayBuffer);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.ScissorTest);
        GL.Scissor(prevScissorBox[0], prevScissorBox[1], prevScissorBox[2], prevScissorBox[3]);
    }

    private static void SetKeyMappings()
    {
        // No explicit key map needed for ImGui 1.89+ — uses AddKeyEvent
    }

    /// <summary>
    /// Map Win32 Virtual Key codes to ImGuiKey values.
    /// </summary>
    private static ImGuiKey TranslateVK(int vk)
    {
        return vk switch
        {
            9  => ImGuiKey.Tab,
            37 => ImGuiKey.LeftArrow,
            39 => ImGuiKey.RightArrow,
            38 => ImGuiKey.UpArrow,
            40 => ImGuiKey.DownArrow,
            33 => ImGuiKey.PageUp,
            34 => ImGuiKey.PageDown,
            36 => ImGuiKey.Home,
            35 => ImGuiKey.End,
            45 => ImGuiKey.Insert,
            46 => ImGuiKey.Delete,
            8  => ImGuiKey.Backspace,
            32 => ImGuiKey.Space,
            13 => ImGuiKey.Enter,
            27 => ImGuiKey.Escape,
            162 => ImGuiKey.LeftCtrl,
            160 => ImGuiKey.LeftShift,
            164 => ImGuiKey.LeftAlt,
            163 => ImGuiKey.RightCtrl,
            161 => ImGuiKey.RightShift,
            165 => ImGuiKey.RightAlt,
            // A-Z (VK 65-90)
            65 => ImGuiKey.A, 66 => ImGuiKey.B, 67 => ImGuiKey.C, 68 => ImGuiKey.D,
            69 => ImGuiKey.E, 70 => ImGuiKey.F, 71 => ImGuiKey.G, 72 => ImGuiKey.H,
            73 => ImGuiKey.I, 74 => ImGuiKey.J, 75 => ImGuiKey.K, 76 => ImGuiKey.L,
            77 => ImGuiKey.M, 78 => ImGuiKey.N, 79 => ImGuiKey.O, 80 => ImGuiKey.P,
            81 => ImGuiKey.Q, 82 => ImGuiKey.R, 83 => ImGuiKey.S, 84 => ImGuiKey.T,
            85 => ImGuiKey.U, 86 => ImGuiKey.V, 87 => ImGuiKey.W, 88 => ImGuiKey.X,
            89 => ImGuiKey.Y, 90 => ImGuiKey.Z,
            // 0-9 (VK 48-57)
            48 => ImGuiKey._0, 49 => ImGuiKey._1, 50 => ImGuiKey._2, 51 => ImGuiKey._3,
            52 => ImGuiKey._4, 53 => ImGuiKey._5, 54 => ImGuiKey._6, 55 => ImGuiKey._7,
            56 => ImGuiKey._8, 57 => ImGuiKey._9,
            _ => ImGuiKey.None
        };
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vertexArray);
        GL.DeleteBuffer(_vertexBuffer);
        GL.DeleteBuffer(_indexBuffer);
        GL.DeleteTexture(_fontTexture);
        GL.DeleteProgram(_shaderProgram);
        ImGui.DestroyContext();
    }
}
