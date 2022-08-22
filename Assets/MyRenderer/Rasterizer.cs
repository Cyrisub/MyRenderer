﻿using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace MyRenderer
{
    [Flags]
    public enum BufferMask
    {
        None = 0,
        Color = 1,
        Depth = 2,
    }

    public struct FrameBuffer
    {
        public NativeArray<Color> ColorBuffer;
        public NativeArray<float> DepthBuffer;

        public Texture2D ScreenRT;

        private Color[] _tempColor;
        private float[] _tempDepth;

        public FrameBuffer(int width, int height)
        {
            int bufferSize = width * height;
            ColorBuffer = new NativeArray<Color>(bufferSize, Allocator.Persistent);
            DepthBuffer = new NativeArray<float>(bufferSize, Allocator.Persistent);
            ScreenRT = new Texture2D(width, height)
            {
                filterMode = FilterMode.Point
            };
            _tempColor = new Color[bufferSize];
            _tempDepth = new float[bufferSize];
        }

        public void Clear(BufferMask mask)
        {
            if (mask.HasFlag(BufferMask.Color))
            {
                Array.Fill(_tempColor, Color.black);
                ColorBuffer.CopyFrom(_tempColor);
            }

            if (mask.HasFlag(BufferMask.Depth))
            {
                Array.Fill(_tempDepth, 0.0f);
                DepthBuffer.CopyFrom(_tempDepth);
            }
        }

        public void Flush()
        {
            ColorBuffer.CopyTo(_tempColor);
            ScreenRT.SetPixels(_tempColor);
            ScreenRT.Apply();
        }

        public void Release()
        {
            ColorBuffer.Dispose();
            DepthBuffer.Dispose();
            ScreenRT = null;
            _tempColor = null;
            _tempDepth = null;
        }
    }

    public class Rasterizer
    {
        public FrameBuffer FrameBuffer;

        private int _width, _height;
        private UniformBuffer _uniforms;

        public Rasterizer(int width, int height)
        {
            _width = width;
            _height = height;

            FrameBuffer = new FrameBuffer(_width, _height);
            _uniforms = new UniformBuffer();
        }

        public void Release()
        {
            FrameBuffer.Release();
        }

        public void Clear()
        {
            Profiler.BeginSample("MyRenderer::Rasterizer::Clear");

            FrameBuffer.Clear(BufferMask.Color | BufferMask.Depth);

            Profiler.EndSample();
        }

        public void SetupUniform(Camera camera, Light mainLight)
        {
            // 着色时在右手坐标系计算，因此 uniforms 的 z 值需要翻转
            var transform = camera.transform;
            _uniforms.WSCameraPos = transform.position;
            _uniforms.WSCameraPos.z *= -1;
            _uniforms.WSLightDir = -mainLight.transform.forward;
            _uniforms.WSLightDir.z *= -1;

            Color lightColor = mainLight.color * mainLight.intensity;
            _uniforms.LightColor = new float4(lightColor.r, lightColor.g, lightColor.b, lightColor.a);

            float3 lookAt = transform.forward.normalized;
            float3 up = transform.up.normalized;
            lookAt.z *= -1;
            up.z *= -1;
            _uniforms.MatView = GetViewMatrix(_uniforms.WSCameraPos, lookAt, up);

            float aspect = (float) _width / _height;
            float near = -camera.nearClipPlane;
            float far = -camera.farClipPlane;
            if (camera.orthographic)
            {
                float height = camera.orthographicSize * 2;
                float width = aspect * height;
                _uniforms.MatProjection = float4x4.Ortho(width, height, near, far);
            }
            else
            {
                // _uniforms.MatProjection = float4x4.PerspectiveFov(camera.fieldOfView, aspect, near, far);
                _uniforms.MatProjection = GetPerspectiveProjectionMatrix(camera.fieldOfView, aspect, near, far);
            }
        }

        public void Draw(RenderProxy proxy)
        {
            Profiler.BeginSample("MyRenderer::Rasterizer::Draw");

            var transform = proxy.transform;
            var position = transform.position;
            position.z *= -1;
            _uniforms.MatModel = float4x4.TRS(position, Quaternion.Inverse(transform.rotation), transform.lossyScale);

            var VaryingsArray = new NativeArray<Varyings>(proxy.mesh.vertexCount, Allocator.TempJob);
            var VS = new VertexShader()
            {
                Attributes = proxy.Attributes,
                Uniforms = _uniforms,
                VaryingsArray = VaryingsArray,
            };
            var VSHandle = VS.Schedule(VaryingsArray.Length, 1);

            var PS = new PixelShader()
            {
                Attributes = proxy.Attributes,
                Uniforms = _uniforms,
                VaryingsArray = VaryingsArray,
                Width = _width,
                Height = _height,
                ColorBuffer = FrameBuffer.ColorBuffer,
                DepthBuffer = FrameBuffer.DepthBuffer,
            };
            var PSHandle = PS.Schedule(proxy.Attributes.Indices.Length, 1, VSHandle);

            VSHandle.Complete();
            PSHandle.Complete();
            VaryingsArray.Dispose();

            Profiler.EndSample();
        }

        public void Flush()
        {
            Profiler.BeginSample("MyRenderer::Rasterizer::Flush");

            FrameBuffer.Flush();

            Profiler.EndSample();
        }

        public static float4x4 GetViewMatrix(float3 eye, float3 lookAt, float3 up)
        {
            float3 camZ = -math.normalize(lookAt);
            float3 camY = math.normalize(up);
            float3 camX = math.cross(camY, camZ);
            camY = math.cross(camZ, camX);

            return new float4x4(
                new float4(camX, 0.0f),
                new float4(camY, 0.0f),
                new float4(camZ, 0.0f),
                new float4(-eye, 1.0f)
            );
        }

        public static float4x4 GetPerspectiveProjectionMatrix(float fov, float aspect, float near, float far)
        {
            float4x4 result = float4x4.identity;
            
            float t = near * math.tan(math.radians(fov * 0.5f));
            float b = -t;
            float r = t * aspect;
            float l = -r;
            float n = -near;
            float f = -far;
            
            result.c0.x = n;
            result.c1.y = n;
            result.c2.z = n + f;
            result.c2.w = -n * f;
            result.c3.z = 1;
            result.c3.w = 0;
            
            Matrix4x4 translate = Matrix4x4.identity;
            translate.SetColumn(3, new Vector4(-(r + l) * 0.5f, -(t + b) * 0.5f, -(n + f) * 0.5f, 1f));
            Matrix4x4 scale = Matrix4x4.identity;
            scale.m00 = 2f / (r - l);
            scale.m11 = 2f / (t - b);
            scale.m22 = 2f / (n - f);
            float4x4 ortho = scale * translate;
            
            return ortho * result;
        }
    }
}