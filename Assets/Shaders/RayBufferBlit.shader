Shader "RayBufferBlit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
		_RayOffset("RayOffset", Float) = 0
		_RayScale("RayScale", Float) = 1
	}

    SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
			#pragma multi_compile __  COPY_MAIN1 COPY_MAIN2
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float3 vertex : POSITION;
                float4 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
				float4 uv : TEXCOORD0;
			};

            v2f vert (appdata v)
            {
                v2f o;
				o.vertex = float4(v.vertex, 1);
                o.uv = v.uv;
                return o;
            }

            sampler2D _MainTex1;
			sampler2D _MainTex2;
			float4 _RayOffset;
			float4 _RayScale;

            fixed4 frag (v2f i) : SV_Target {
#ifdef COPY_MAIN1
				float2 uv = i.vertex.xy / _ScreenParams.xy;
				return tex2D(_MainTex1, float2(1 - uv.y, uv.x));
#elif COPY_MAIN2
				float2 uv = i.vertex.xy / _ScreenParams.xy;
				return tex2D(_MainTex2, float2(1 - uv.y, uv.x));
#else
				float x = i.uv.x / (i.uv.x + i.uv.y);
				x = _RayOffset[i.uv.w] + x * _RayScale[i.uv.w];

				float y1 = 1 - (i.vertex.y / _ScreenParams.y);
				float y2 = (i.vertex.x / _ScreenParams.x);
				float4 sample1 = tex2D(_MainTex1, float2(y1, x));
				float4 sample2 = tex2D(_MainTex2, float2(y2, x));
				return lerp(sample1, sample2, i.uv.w > 1);
#endif
            }
            ENDCG
        }
    }
}
