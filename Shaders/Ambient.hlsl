Texture2D Source : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer Parameters : register(b0)
{
    float4 Crop;       // origin xy, size zw in upright desktop coordinates
    float4 Transform;  // flip x, flip y, DXGI rotation, unused
    float4 Blur;       // offset xy, Gaussian flag, unused
    float4 Effects;    // strength, brightness, noise amount, veil flag
    float4 Animation;  // noise frame, unused
    float4 Retained;   // source crop boundaries: left, top, right, bottom
    float4 VeilColor;
    float4 Repeat;     // columns, rows, mode (0 off, 1 repeat, 2 mirror)
    float4 Gradient;   // mode, opacity, fade start, angle radians
    float4 GradientTint;
    float4 GradientEdges;
    float4 ColorPipeline; // linear working path, FP16 source, HDR destination, FP16 output
    float4 GradientStartTint;
    float4 GradientOptions; // end stop, falloff, start opacity, overall opacity
    float4 EdgeWidths;
    float4 EdgeStrengths;
    float4 GradientControl; // independent-edge widths enabled
};

struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };

float3 SrgbToLinear(float3 color)
{
    float3 high = step(0.04045, color);
    return pow(max((color + 0.055) / 1.055, 0), 2.4) * high + color / 12.92 * (1 - high);
}
float3 LinearToSrgb(float3 color)
{
    float3 high = step(0.0031308, color);
    return (1.055 * pow(max(color, 0), 1 / 2.4) - 0.055) * high + color * 12.92 * (1 - high);
}

Vertex FullscreenVS(uint id : SV_VertexID)
{
    Vertex v;
    v.uv = float2((id << 1) & 2, id & 2);
    v.position = float4(v.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}

float2 UprightSourceUV(float2 uv)
{
    if (Transform.x > 0) uv.x = 1 - uv.x;
    if (Transform.y > 0) uv.y = 1 - uv.y;
    if (Repeat.z > 0)
    {
        float2 p = min(uv, 0.999999) * Repeat.xy;
        float2 tile = floor(p); uv = frac(p);
        if (Repeat.z > 1) uv = lerp(uv, 1 - uv, fmod(tile, 2));
    }
    uv = Crop.xy + uv * Crop.zw;
    return uv;
}

bool RemovedByCrop(float2 uv)
{
    return Animation.y > 0 && (uv.x < Retained.x || uv.y < Retained.y || uv.x >= Retained.z || uv.y >= Retained.w);
}

float4 CropPS(Vertex v) : SV_Target
{
    float2 uv = UprightSourceUV(v.uv);
    if (RemovedByCrop(uv)) return float4(0, 0, 0, 1);
    // Desktop Duplication returns an unrotated texture for portrait displays.
    if (Transform.z == 2) uv = float2(uv.y, 1 - uv.x);
    else if (Transform.z == 3) uv = 1 - uv;
    else if (Transform.z == 4) uv = float2(1 - uv.y, uv.x);
    float4 color = Source.SampleLevel(LinearClamp, uv, 0);
    if (ColorPipeline.x > 0 && ColorPipeline.y == 0)
        color.rgb = SrgbToLinear(color.rgb);
    return color;
}

float4 BlurPS(Vertex v) : SV_Target
{
    float4 color = 0;
    float total = 0;
    // Fixed work per pixel, independent of the selected blur radius.
    [unroll] for (int i = -16; i <= 16; ++i)
    {
        float x = i / 16.0;
        float weight = Blur.z > 0 ? exp(-4.5 * x * x) : 1;
        color += Source.SampleLevel(LinearClamp, v.uv + Blur.xy * x, 0) * weight;
        total += weight;
    }
    return color / total;
}

float Noise(uint2 pixel, uint frame)
{
    uint hash = pixel.x * 1973u + pixel.y * 9277u + frame * 26699u + 911u;
    hash = (hash ^ (hash >> 16)) * 2246822519u;
    hash = (hash ^ (hash >> 13)) * 3266489917u;
    hash ^= hash >> 16;
    return (hash & 65535u) / 65535.0;
}

float4 CompositePS(Vertex v) : SV_Target
{
    bool removed = Effects.w == 0 && RemovedByCrop(UprightSourceUV(v.uv));
    float3 veil = float3(16, 21, 31) / 255.0;
    float3 chosenVeil = VeilColor.rgb;
    float3 gradientColor = GradientTint.rgb;
    float3 gradientStartColor = GradientStartTint.rgb;
    if (ColorPipeline.x > 0)
    {
        veil = SrgbToLinear(veil);
        chosenVeil = SrgbToLinear(chosenVeil);
        gradientColor = SrgbToLinear(gradientColor);
        gradientStartColor = SrgbToLinear(gradientStartColor);
    }
    float3 color;
    if (Effects.w > 0)
        color = chosenVeil * Effects.x;
    else
        color = lerp(Source.SampleLevel(LinearClamp, v.uv, 0).rgb * Effects.y * Effects.x, veil, 0.06);
    if (Effects.z > 0)
        color = lerp(color, Noise(uint2(floor(v.position.xy / max(Animation.z, 1))), uint(Animation.x)).xxx, Effects.z);
    if (removed) color = 0;
    if (Gradient.x > 0)
    {
        float alpha = 0;
        float3 tint = gradientColor;
        float position = 0;
        if (Gradient.x > 1 && GradientControl.x > 0)
        {
            float4 distances = float4(v.uv.x, 1 - v.uv.x, v.uv.y, 1 - v.uv.y);
            float4 edge = saturate(1 - distances / max(EdgeWidths, 0.001));
            edge = pow(edge * edge * (3 - 2 * edge), GradientOptions.y) * EdgeStrengths * GradientEdges * step(0.000001, EdgeWidths);
            alpha = max(max(edge.x, edge.y), max(edge.z, edge.w)) * Gradient.y;
        }
        else
        {
            if (Gradient.x < 2)
            {
                float2 direction = float2(cos(Gradient.w), sin(Gradient.w));
                position = 0.5 + dot(v.uv - 0.5, direction) / (abs(direction.x) + abs(direction.y));
            }
            else
            {
                if (GradientEdges.x > 0) position = max(position, 1 - 2 * v.uv.x);
                if (GradientEdges.y > 0) position = max(position, 2 * v.uv.x - 1);
                if (GradientEdges.z > 0) position = max(position, 1 - 2 * v.uv.y);
                if (GradientEdges.w > 0) position = max(position, 2 * v.uv.y - 1);
            }
            float t = saturate((position - Gradient.z) / max(0.01, GradientOptions.x - Gradient.z));
            t = pow(t * t * (3 - 2 * t), GradientOptions.y);
            alpha = lerp(Gradient.x < 2 ? GradientOptions.z : 0, Gradient.y, t);
            if (Gradient.x < 2) tint = lerp(gradientStartColor, gradientColor, t);
        }
        color = lerp(color, tint, alpha * GradientOptions.w);
    }
    if (ColorPipeline.x > 0)
    {
        // scRGB uses 1.0 = 80 nits. Preserve HDR highlights on HDR outputs;
        // convert HDR source to an SDR white reference and compress highlights
        // when the destination is SDR (or lacks FP16/scRGB presentation).
        if (ColorPipeline.y > 1 && ColorPipeline.z == 0)
        { color = max(color, 0) / 2.5375; color = color / (1 + color); }
        if (ColorPipeline.w == 0)
            color = LinearToSrgb(color);
        return float4(ColorPipeline.w > 0 ? max(color, 0) : saturate(color), 1);
    }
    return float4(saturate(color), 1);
}
