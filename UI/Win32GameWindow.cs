using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;

namespace PhotoOrganizer.UI;

/// <summary>
/// Pure Win32 + WGL window base class. Zero GLFW dependency.
/// Implements IBindingsContext so OpenTK.Graphics.OpenGL4 loads its function pointers
/// from wglGetProcAddress + opengl32.dll.
/// </summary>
public class Win32GameWindow : OpenTK.IBindingsContext, IDisposable
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd,
        uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool PostQuitMessage(int nExitCode);

    [DllImport("gdi32.dll")]
    private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("gdi32.dll")]
    private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("gdi32.dll", EntryPoint = "SwapBuffers")]
    private static extern bool SwapBuffers_GDI(IntPtr hdc);

    [DllImport("opengl32.dll")]
    private static extern IntPtr wglCreateContext(IntPtr hdc);

    [DllImport("opengl32.dll")]
    private static extern bool wglDeleteContext(IntPtr hglrc);

    [DllImport("opengl32.dll")]
    private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);

    [DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress")]
    private static extern IntPtr wglGetProcAddress_raw(string lpszProc);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("user32.dll")]  private static extern bool  OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")]  private static extern bool  CloseClipboard();
    [DllImport("user32.dll")]  private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool  GlobalUnlock(IntPtr hMem);
    [DllImport("user32.dll")]  private static extern short GetKeyState(int nVirtKey);

    // ── Structs ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits, cRedShift;
        public byte cGreenBits, cGreenShift;
        public byte cBlueBits, cBlueShift;
        public byte cAlphaBits, cAlphaShift;
        public byte cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte cDepthBits, cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    // ── WGL extension function pointers ──────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WglCreateContextAttribsARB(IntPtr hDC, IntPtr hShareContext, int[] attribList);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool WglChoosePixelFormatARB(IntPtr hdc, int[] piAttribIList,
        float[]? pfAttribFList, uint nMaxFormats, int[] piFormats, out uint nNumFormats);

    // ── Constants ─────────────────────────────────────────────────────────────

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint CS_OWNDC = 0x0020;
    private const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    private const uint PFD_SUPPORT_OPENGL = 0x00000020;
    private const uint PFD_DOUBLEBUFFER   = 0x00000001;
    private const byte PFD_TYPE_RGBA      = 0;
    private const byte PFD_MAIN_PLANE     = 0;

    // WM_ messages
    private const uint WM_DESTROY   = 0x0002;
    private const uint WM_SIZE      = 0x0005;
    private const uint WM_CLOSE     = 0x0010;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP   = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP   = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP   = 0x0208;
    private const uint WM_MOUSEWHEEL  = 0x020A;
    private const uint WM_CHAR        = 0x0102;
    private const uint WM_KEYDOWN     = 0x0100;
    private const uint WM_KEYUP       = 0x0101;
    private const uint WM_SYSKEYDOWN  = 0x0104;
    private const uint WM_SYSKEYUP    = 0x0105;
    private const uint WM_QUIT        = 0x0012;

    // WGL context attrib ids
    private const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    private const int WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    private const int WGL_CONTEXT_PROFILE_MASK_ARB  = 0x9126;
    private const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB = 0x00000001;

    // WGL pixel format attrib ids
    private const int WGL_DRAW_TO_WINDOW_ARB = 0x2001;
    private const int WGL_SUPPORT_OPENGL_ARB = 0x2010;
    private const int WGL_DOUBLE_BUFFER_ARB  = 0x2011;
    private const int WGL_PIXEL_TYPE_ARB     = 0x2013;
    private const int WGL_TYPE_RGBA_ARB      = 0x202B;
    private const int WGL_COLOR_BITS_ARB     = 0x2014;
    private const int WGL_DEPTH_BITS_ARB     = 0x2022;
    private const int WGL_STENCIL_BITS_ARB   = 0x2023;

    // ── Fields ────────────────────────────────────────────────────────────────

    private IntPtr _hWnd;
    private IntPtr _hDC;
    private IntPtr _hGLRC;
    private IntPtr _opengl32;

    private bool _running;
    private bool _disposed;

    // Keep the delegate alive for the lifetime of the window
    private static WndProcDelegate? _wndProcDelegate;
    // Instance reference per HWND (single window scenario is fine with a static)
    private static Win32GameWindow? _instance;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ── Input state ──────────────────────────────────────────────────────────

    public float MouseX { get; private set; }
    public float MouseY { get; private set; }
    public bool[] MouseButtons { get; } = new bool[3]; // 0=left,1=right,2=middle
    public float ScrollDelta { get; private set; }
    public Queue<char> PendingChars { get; } = new Queue<char>();

    private readonly bool[] _keyStates = new bool[256];
    public bool IsKeyDown(int vk) => (uint)vk < 256 && _keyStates[vk];

    // ── Window dimensions ─────────────────────────────────────────────────────

    public int ClientWidth { get; private set; } = 900;
    public int ClientHeight { get; private set; } = 650;

    // ── IBindingsContext ─────────────────────────────────────────────────────

    public IntPtr GetProcAddress(string procName)
    {
        // Try WGL extension first (covers OpenGL > 1.1)
        IntPtr addr = wglGetProcAddress_raw(procName);
        if (addr != IntPtr.Zero && addr != new IntPtr(1) && addr != new IntPtr(2) &&
            addr != new IntPtr(3) && addr != new IntPtr(-1))
            return addr;

        // Fallback: opengl32.dll for core 1.1 functions
        if (_opengl32 != IntPtr.Zero)
        {
            addr = GetProcAddress(_opengl32, procName);
            if (addr != IntPtr.Zero)
                return addr;
        }

        return IntPtr.Zero;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void SwapBuffers() => SwapBuffers_GDI(_hDC);

    public void Close() => _running = false;

    // ── Virtual lifecycle ─────────────────────────────────────────────────────

    protected virtual void OnLoad() { }
    protected virtual void OnUpdate(double deltaTime) { }
    protected virtual void OnRender(double deltaTime) { }
    protected virtual void OnResize(int width, int height) { }
    protected virtual void OnUnload() { }

    // ── Run ───────────────────────────────────────────────────────────────────

    public void Run()
    {
        _opengl32 = LoadLibrary("opengl32.dll");

        CreateWglWindow();

        // Load OpenTK GL bindings using our IBindingsContext
        GL.LoadBindings(this);

        OnLoad();

        _running = true;
        var sw = Stopwatch.StartNew();
        double lastTime = sw.Elapsed.TotalSeconds;

        while (_running)
        {
            // Reset per-frame scroll
            ScrollDelta = 0f;

            // Pump messages
            while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, 1 /*PM_REMOVE*/))
            {
                if (msg.message == WM_QUIT)
                {
                    _running = false;
                    break;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (!_running) break;

            double now = sw.Elapsed.TotalSeconds;
            double dt = now - lastTime;
            lastTime = now;

            try
            {
                OnUpdate(dt);
                OnRender(dt);
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    $"{ex.GetType().Name}:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "Render Error", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
                _running = false;
                break;
            }

            // ~60 fps cap
            double elapsed = sw.Elapsed.TotalSeconds - now;
            int sleep = (int)((1.0 / 60.0 - elapsed) * 1000.0);
            if (sleep > 1) Thread.Sleep(sleep);
        }

        OnUnload();
        Cleanup();
    }

    // ── WGL window creation ───────────────────────────────────────────────────

    private void CreateWglWindow()
    {
        _instance = this;

        // Register window class
        _wndProcDelegate = WndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = GetModuleHandle(null),
            lpszClassName = "Win32GameWindow"
        };
        RegisterClassEx(ref wc);

        // ── Step 1: dummy window + legacy context ─────────────────────────────
        IntPtr dummyHwnd = CreateWindowEx(0, "Win32GameWindow", "Dummy",
            WS_OVERLAPPEDWINDOW, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        IntPtr dummyDC = GetDC(dummyHwnd);

        var pfd = MakeBasicPFD();
        int fmt = ChoosePixelFormat(dummyDC, ref pfd);
        SetPixelFormat(dummyDC, fmt, ref pfd);

        IntPtr dummyCtx = wglCreateContext(dummyDC);
        wglMakeCurrent(dummyDC, dummyCtx);

        // ── Step 2: load WGL extensions ──────────────────────────────────────
        WglCreateContextAttribsARB? createCtxAttribs = null;
        WglChoosePixelFormatARB? choosePixFmt = null;

        IntPtr p1 = wglGetProcAddress_raw("wglCreateContextAttribsARB");
        if (p1 != IntPtr.Zero)
            createCtxAttribs = Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribsARB>(p1);

        IntPtr p2 = wglGetProcAddress_raw("wglChoosePixelFormatARB");
        if (p2 != IntPtr.Zero)
            choosePixFmt = Marshal.GetDelegateForFunctionPointer<WglChoosePixelFormatARB>(p2);

        // ── Step 3: real window ───────────────────────────────────────────────
        int screenW = GetSystemMetrics(0); // SM_CXSCREEN
        int screenH = GetSystemMetrics(1); // SM_CYSCREEN
        int winX = (screenW - ClientWidth) / 2;
        int winY = (screenH - ClientHeight) / 2;

        // We need outer window size, not client size. Use AdjustWindowRect approximation (+16/+39)
        _hWnd = CreateWindowEx(0, "Win32GameWindow", "PhotoOrganizer",
            WS_OVERLAPPEDWINDOW,
            winX, winY, ClientWidth + 16, ClientHeight + 39,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        _hDC = GetDC(_hWnd);

        // Choose pixel format for real window
        int realFmt;
        if (choosePixFmt != null)
        {
            int[] attribs = new int[]
            {
                WGL_DRAW_TO_WINDOW_ARB, 1,
                WGL_SUPPORT_OPENGL_ARB, 1,
                WGL_DOUBLE_BUFFER_ARB,  1,
                WGL_PIXEL_TYPE_ARB, WGL_TYPE_RGBA_ARB,
                WGL_COLOR_BITS_ARB, 32,
                WGL_DEPTH_BITS_ARB, 24,
                WGL_STENCIL_BITS_ARB, 8,
                0
            };
            int[] formats = new int[1];
            choosePixFmt(_hDC, attribs, null, 1, formats, out uint numFormats);
            realFmt = numFormats > 0 ? formats[0] : fmt;
        }
        else
        {
            realFmt = ChoosePixelFormat(_hDC, ref pfd);
        }

        var pfd2 = MakeBasicPFD();
        SetPixelFormat(_hDC, realFmt, ref pfd2);

        // ── Step 4: real OpenGL 3.3 core context ──────────────────────────────
        if (createCtxAttribs != null)
        {
            int[] ctxAttribs = new int[]
            {
                WGL_CONTEXT_MAJOR_VERSION_ARB, 3,
                WGL_CONTEXT_MINOR_VERSION_ARB, 3,
                WGL_CONTEXT_PROFILE_MASK_ARB, WGL_CONTEXT_CORE_PROFILE_BIT_ARB,
                0
            };
            _hGLRC = createCtxAttribs(_hDC, IntPtr.Zero, ctxAttribs);
        }

        if (_hGLRC == IntPtr.Zero)
        {
            // Fallback to legacy context if ARB not available
            _hGLRC = wglCreateContext(_hDC);
        }

        // Clean up dummy
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        wglDeleteContext(dummyCtx);
        ReleaseDC(dummyHwnd, dummyDC);
        DestroyWindow(dummyHwnd);

        // Make real context current
        wglMakeCurrent(_hDC, _hGLRC);

        ShowWindow(_hWnd, 5 /*SW_SHOW*/);
        UpdateWindow(_hWnd);
    }

    private static PIXELFORMATDESCRIPTOR MakeBasicPFD()
    {
        return new PIXELFORMATDESCRIPTOR
        {
            nSize    = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags  = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            iPixelType = PFD_TYPE_RGBA,
            cColorBits = 32,
            cDepthBits = 24,
            cStencilBits = 8,
            iLayerType = PFD_MAIN_PLANE
        };
    }

    // ── WndProc ───────────────────────────────────────────────────────────────

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _instance;
        if (self == null)
            return DefWindowProc(hWnd, msg, wParam, lParam);

        switch (msg)
        {
            case WM_CLOSE:
                self._running = false;
                PostQuitMessage(0);
                return IntPtr.Zero;

            case WM_DESTROY:
                if (hWnd == self._hWnd)
                    PostQuitMessage(0);
                return IntPtr.Zero;

            case WM_SIZE:
            {
                int w = (int)(lParam.ToInt64() & 0xFFFF);
                int h = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (w > 0 && h > 0)
                {
                    self.ClientWidth = w;
                    self.ClientHeight = h;
                    self.OnResize(w, h);
                }
                return IntPtr.Zero;
            }

            case WM_MOUSEMOVE:
            {
                long lp = lParam.ToInt64();
                self.MouseX = (float)(lp & 0xFFFF);
                self.MouseY = (float)((lp >> 16) & 0xFFFF);
                return IntPtr.Zero;
            }

            case WM_LBUTTONDOWN:
                self.MouseButtons[0] = true;
                SetCapture(hWnd);
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                self.MouseButtons[0] = false;
                ReleaseCapture();
                return IntPtr.Zero;

            case WM_RBUTTONDOWN:
                self.MouseButtons[1] = true;
                SetCapture(hWnd);
                return IntPtr.Zero;
            case WM_RBUTTONUP:
                self.MouseButtons[1] = false;
                ReleaseCapture();
                return IntPtr.Zero;

            case WM_MBUTTONDOWN:
                self.MouseButtons[2] = true;
                SetCapture(hWnd);
                return IntPtr.Zero;
            case WM_MBUTTONUP:
                self.MouseButtons[2] = false;
                ReleaseCapture();
                return IntPtr.Zero;

            case WM_MOUSEWHEEL:
            {
                // High word of wParam = delta; positive = forward/up
                long wp = wParam.ToInt64();
                short delta = (short)((wp >> 16) & 0xFFFF);
                self.ScrollDelta += delta / 120.0f;
                return IntPtr.Zero;
            }

            case WM_CHAR:
            {
                char c = (char)(wParam.ToInt64() & 0xFFFF);
                if (c >= 32) // ignore control chars
                    self.PendingChars.Enqueue(c);
                return IntPtr.Zero;
            }

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            {
                int vk = (int)(wParam.ToInt64() & 0xFF);
                if (vk < 256) self._keyStates[vk] = true;

                // Ctrl+V — read clipboard and push chars into PendingChars so
                // ImGui's active input field receives them exactly like typed text.
                if (vk == 0x56 && (GetKeyState(0x11) & 0x8000) != 0) // V + Ctrl
                {
                    if (OpenClipboard(IntPtr.Zero))
                    {
                        try
                        {
                            var hData = GetClipboardData(13u); // CF_UNICODETEXT
                            if (hData != IntPtr.Zero)
                            {
                                var ptr = GlobalLock(hData);
                                if (ptr != IntPtr.Zero)
                                {
                                    try
                                    {
                                        var text = System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr) ?? "";
                                        foreach (char c in text)
                                            if (c >= 32) self.PendingChars.Enqueue(c);
                                    }
                                    finally { GlobalUnlock(hData); }
                                }
                            }
                        }
                        finally { CloseClipboard(); }
                    }
                }

                return DefWindowProc(hWnd, msg, wParam, lParam);
            }

            case WM_KEYUP:
            case WM_SYSKEYUP:
            {
                int vk = (int)(wParam.ToInt64() & 0xFF);
                if (vk < 256) self._keyStates[vk] = false;
                return DefWindowProc(hWnd, msg, wParam, lParam);
            }
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void Cleanup()
    {
        if (_hGLRC != IntPtr.Zero)
        {
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(_hGLRC);
            _hGLRC = IntPtr.Zero;
        }
        if (_hDC != IntPtr.Zero && _hWnd != IntPtr.Zero)
        {
            ReleaseDC(_hWnd, _hDC);
            _hDC = IntPtr.Zero;
        }
        if (_hWnd != IntPtr.Zero)
        {
            DestroyWindow(_hWnd);
            _hWnd = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Cleanup();
        }
        GC.SuppressFinalize(this);
    }

    ~Win32GameWindow() => Cleanup();
}
