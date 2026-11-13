// Facet3D viewport shader (shader model 4.0 so it runs at feature level 10 on Xbox in app mode).

cbuffer Frame : register(b0)
{
    row_major float4x4 World;
    row_major float4x4 ViewProj;
    float4 Tint;      // rgb = tint colour, a = tint amount
    float4 LightDir;  // xyz = light direction, w = 1 lit / 0 unlit
    float4 Params;    // x = alpha multiplier
};

struct VSIn
{
    float3 pos : POSITION;
    float3 nrm : NORMAL;
    float4 col : COLOR;
};

struct PSIn
{
    float4 pos : SV_POSITION;
    float3 nrm : NORMAL;
    float4 col : COLOR;
};

PSIn VS(VSIn i)
{
    PSIn o;
    float4 w = mul(float4(i.pos, 1.0), World);
    o.pos = mul(w, ViewProj);
    o.nrm = mul(float4(i.nrm, 0.0), World).xyz;
    o.col = i.col;
    return o;
}

float4 PS(PSIn i) : SV_TARGET
{
    float3 c = i.col.rgb;
    if (LightDir.w > 0.5)
    {
        float len = length(i.nrm);
        float3 n = len > 1e-6 ? i.nrm / len : float3(0, 1, 0);
        float key = abs(dot(n, -LightDir.xyz));
        float fill = abs(dot(n, normalize(float3(-0.4, 0.3, 0.8)))) * 0.25;
        float hemi = lerp(0.18, 0.32, n.y * 0.5 + 0.5);
        c = c * saturate(hemi + key * 0.65 + fill);
    }
    c = lerp(c, Tint.rgb, Tint.a);
    return float4(c, i.col.a * Params.x);
}
