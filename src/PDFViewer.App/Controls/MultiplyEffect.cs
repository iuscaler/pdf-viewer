using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PDFViewer.App.Controls;

/// <summary>
/// 곱하기(Multiply) 합성 픽셀 셰이더. 요소가 그린 내용(Input)에 Overlay 브러시의 색을 곱한다.
/// WPF에는 곱하기 합성이 없어서, 형광펜을 그리는 동안에도 Edge처럼 아래 글자가 선명하게 보이도록 GPU에서 직접 합성한다.
/// </summary>
/// <remarks>
/// 셰이더는 Windows에 기본으로 들어 있는 d3dcompiler_47.dll로 처음 쓸 때 한 번 컴파일한다.
/// 컴파일하지 못하는 환경이면 <see cref="IsSupported"/>가 false이고, 호출하는 쪽은 반투명 방식으로 대신 그린다.
/// </remarks>
internal sealed class MultiplyEffect : ShaderEffect
{
    // WPF가 셰이더에 넘기는 색은 알파가 곱해진 값이다. 형광펜 색 c, 불투명도 a일 때
    // 곱할 값은 lerp(1, c, a) = 1 - a + a*c = 1 - o.a + o.rgb 가 된다(형광펜이 없는 곳은 1이라 그대로).
    private const string ShaderSource = """
        sampler2D input : register(s0);
        sampler2D overlay : register(s1);

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float4 color = tex2D(input, uv);
            float4 o = tex2D(overlay, uv);
            color.rgb *= 1 - o.a + o.rgb;
            return color;
        }
        """;

    private static readonly Lazy<PixelShader?> Shader = new(CompileShader);

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(MultiplyEffect), 0);

    public static readonly DependencyProperty OverlayProperty =
        RegisterPixelShaderSamplerProperty(nameof(Overlay), typeof(MultiplyEffect), 1);

    public MultiplyEffect()
    {
        PixelShader = Shader.Value;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(OverlayProperty);
    }

    /// <summary>이 환경에서 셰이더를 쓸 수 있는지 여부.</summary>
    public static bool IsSupported => Shader.Value is not null;

    /// <summary>요소 자신이 그린 내용. 설정하지 않으면 자동으로 들어간다.</summary>
    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    /// <summary>곱할 색. 요소와 같은 크기로 맞춰지며, 투명한 곳은 원래 색 그대로 남는다.</summary>
    public Brush Overlay
    {
        get => (Brush)GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    private static PixelShader? CompileShader()
    {
        try
        {
            var bytecode = D3DCompiler.Compile(ShaderSource, "main", "ps_2_0");
            var shader = new PixelShader();
            shader.SetStreamSource(new MemoryStream(bytecode));
            shader.Freeze();
            return shader;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or InvalidOperationException or COMException)
        {
            Debug.WriteLine($"[MultiplyEffect] 셰이더를 컴파일하지 못했습니다: {ex.Message}");
            return null;
        }
    }

    /// <summary>d3dcompiler_47.dll의 D3DCompile로 HLSL을 셰이더 바이트코드로 컴파일한다.</summary>
    private static unsafe class D3DCompiler
    {
        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern int D3DCompile(byte[] sourceData, nint sourceSize, string? sourceName, nint defines,
            nint include, string entryPoint, string target, uint flags1, uint flags2, out nint code,
            out nint errorMessages);

        public static byte[] Compile(string source, string entryPoint, string target)
        {
            var bytes = Encoding.ASCII.GetBytes(source);
            var hr = D3DCompile(bytes, bytes.Length, null, 0, 0, entryPoint, target, 0, 0, out var code,
                out var errors);
            try
            {
                if (hr < 0 || code == 0)
                {
                    var message = errors != 0 ? Encoding.ASCII.GetString(ReadBlob(errors)) : $"HRESULT 0x{hr:X8}";
                    throw new InvalidOperationException(message);
                }

                return ReadBlob(code);
            }
            finally
            {
                Release(code);
                Release(errors);
            }
        }

        // ID3DBlob: IUnknown(QueryInterface, AddRef, Release) 다음에 GetBufferPointer, GetBufferSize
        private static byte[] ReadBlob(nint blob)
        {
            var vtable = *(nint**)blob;
            var getPointer = (delegate* unmanaged[Stdcall]<nint, nint>)vtable[3];
            var getSize = (delegate* unmanaged[Stdcall]<nint, nuint>)vtable[4];
            return new ReadOnlySpan<byte>((void*)getPointer(blob), checked((int)getSize(blob))).ToArray();
        }

        private static void Release(nint blob)
        {
            if (blob != 0)
            {
                var vtable = *(nint**)blob;
                ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(blob);
            }
        }
    }
}
