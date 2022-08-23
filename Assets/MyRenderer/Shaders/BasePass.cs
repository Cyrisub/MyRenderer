﻿using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace MyRenderer.Shaders
{
    public static class BasePass
    {
        [BurstCompile]
        public struct VertexShader : IJobParallelFor
        {
            [ReadOnly] public Attributes Attributes;
            [ReadOnly] public UniformBuffer Uniforms;

            public NativeArray<Varyings> VaryingsArray;

            public void Execute(int index)
            {
                Varyings varyings;

                var OSPos = new float4(Attributes.Position[index], 1.0f);
                OSPos.z *= -1;
                varyings.CSPos = math.mul(Uniforms.MatMVP, OSPos);
                varyings.WSPos = math.mul(Uniforms.MatModel, OSPos).xyz;
                var OSNormal = new float4(Attributes.Normal[index], 0.0f);
                OSNormal.z *= -1;
                varyings.WSNormal = math.mul(Uniforms.MatNormal, OSNormal).xyz;
                varyings.UV0 = Attributes.UV[index];

                VaryingsArray[index] = varyings;
            }
        }

        [BurstCompile]
        public struct PixelShader : IJobParallelFor
        {
            [ReadOnly] public Attributes Attributes;
            [ReadOnly] public UniformBuffer Uniforms;
            [ReadOnly] public NativeArray<Varyings> VaryingsArray;
            [ReadOnly] public int Width, Height;

            [NativeDisableParallelForRestriction] public NativeArray<Color> ColorBuffer;
            [NativeDisableParallelForRestriction] public NativeArray<float> DepthBuffer;

            public void Execute(int index)
            {
                var indice = Attributes.Indices[index];
                var verts = new NativeArray<Varyings>(3, Allocator.Temp);
                for (int i = 0; i < 3; ++i)
                {
                    verts[i] = VaryingsArray[indice[i]];
                }

                var pos = new NativeArray<float3>(3, Allocator.Temp);
                for (int i = 0; i < 3; ++i)
                {
                    pos[i] = verts[i].CSPos.xyz / verts[i].CSPos.w;
                }

                if (Common.Clipped(pos)) return;
                if (Common.Backface(pos)) return;

                var screen = new float2(Width - 1, Height - 1) * 0.5f;
                for (int i = 0; i < 3; ++i)
                {
                    pos[i] = new float3((pos[i].xy + new float2(1.0f, 1.0f)) * screen, pos[i].z * 0.5f + 0.5f);
                }

                var t = new Triangle(pos, verts);
                DrawTriangle(pos, t);

                t.Release();
                pos.Dispose();
                verts.Dispose();
            }

            private void DrawTriangle(NativeArray<float3> SSPos, Triangle t)
            {
                t.GetScreenBounds(new int2(Width, Height), out var minCoord, out var maxCoord);

                for (int y = minCoord.y; y < maxCoord.y; ++y)
                {
                    for (int x = minCoord.x; x < maxCoord.x; ++x)
                    {
                        float3 pos = new float3(x + 0.5f, y + 0.5f, 0.0f);
                        var baryCoord = Common.ComputeBarycentric2D(pos.x, pos.y, SSPos);
                        if (baryCoord.x < 0 || baryCoord.y < 0 || baryCoord.z < 0) continue;

                        var ws = new float3(t.Verts[0].SSPos.w, t.Verts[1].SSPos.w, t.Verts[2].SSPos.w);
                        var zs = new float3(t.Verts[0].SSPos.z, t.Verts[1].SSPos.z, t.Verts[2].SSPos.z);
                        var co = baryCoord / ws;
                        float z = 1.0f / math.csum(co);
                        pos.z = z * math.csum(co * zs);

                        int index = Common.GetIndex(x, y, Width);
                        if (pos.z < DepthBuffer[index]) continue;

                        DepthBuffer[index] = pos.z;
                        t.Interpolate(pos, co * z, out var Vert);
                        ColorBuffer[index] = BlinnPhong(Vert);
                    }
                }
            }

            private Color BlinnPhong(TriangleVert vert)
            {
                float3 viewDir = math.normalize(Uniforms.WSCameraPos - vert.WSPos);
                float3 halfVec = math.normalize(Uniforms.WSLightDir + viewDir);
                float dotNL = math.dot(vert.WSNormal, Uniforms.WSLightDir);
                float dotNH = math.dot(vert.WSNormal, halfVec);

                float4 ks = new float4(0.7937f, 0.7937f, 0.7937f, 1.0f);
                float4 diffuse = vert.Color * Uniforms.LightColor * math.max(0, dotNL);
                float4 specular = ks * Uniforms.LightColor * math.pow(math.max(0, dotNH), 150);

                float4 color = diffuse + specular;
                return new Color(color.x, color.y, color.z, color.w);
            }
        }
    }
}