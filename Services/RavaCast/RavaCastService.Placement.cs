using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using GameControl = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GameKernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using GameRenderTargetManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;
using Dalamud.Plugin.Services;
using MessagePack;
using Microsoft.Extensions.Logging;
using RavaCast.Configuration;
using RavaCast.Services.Mediator;
using RavaCast.Services.Mesh;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RavaCast.Services.RavaCast;

public sealed partial class RavaCastService
{

    public RavaCastPlane BuildPlane(string screenName, Vector3 centre, float width, float height, float yawRadians, float pitchRadians = 0f)
    {
        width = Math.Max(0.1f, width);
        height = Math.Max(0.1f, height);
        yawRadians = WrapRadians(yawRadians);
        pitchRadians = Math.Clamp(float.IsFinite(pitchRadians) ? pitchRadians : 0f, DegreesToRadians(-80f), DegreesToRadians(80f));

        // Yaw represents the direction from the screen toward the viewer. Right must be the
        // viewer's right, otherwise the projected browser texture is readable only from the
        // back side and appears horizontally reversed from the player/camera side.
        // Pitch tilts the vertical axis around that right vector, allowing the top edge to lean
        // toward or away from the viewer while preserving the same four-corner wire format.
        var right = ScreenRightFromYaw(yawRadians);
        var normal = ScreenNormalFromYaw(yawRadians);
        var up = (Vector3.UnitY * MathF.Cos(pitchRadians)) + (normal * MathF.Sin(pitchRadians));
        if (!IsFinite(up) || up.LengthSquared() <= 0.0001f) up = Vector3.UnitY;
        else up = Vector3.Normalize(up);

        var halfRight = right * (width / 2f);
        var halfUp = up * (height / 2f);
        return new RavaCastPlane(string.IsNullOrWhiteSpace(screenName) ? "RavaCast Screen" : screenName.Trim(),
            centre - halfRight + halfUp,
            centre + halfRight + halfUp,
            centre + halfRight - halfUp,
            centre - halfRight - halfUp);
    }

    public bool TryGetPlayerSuggestedPlacement(out Vector3 centre, out float yaw)
    {
        centre = Vector3.Zero;
        yaw = 0f;
        if (!_clientState.IsLoggedIn || _objects.LocalPlayer is null) return false;
        var player = _objects.LocalPlayer;
        var forward = new Vector3(MathF.Sin(player.Rotation), 0, MathF.Cos(player.Rotation));
        centre = player.Position + forward * 2.35f + Vector3.UnitY * 1.45f;
        yaw = TryGetScreenYawFacingCameraOrPlayer(centre, out var facingYaw) ? facingYaw : WrapRadians(player.Rotation + MathF.PI);
        return true;
    }

    public unsafe bool TryPickScreenPlacementFromCursor(Vector2 screenPoint, Vector2 viewportSize, float preferredCentreY, float screenHeight, out Vector3 centre, out float yaw, out string error)
    {
        centre = Vector3.Zero;
        yaw = 0f;
        error = string.Empty;

        if (!_clientState.IsLoggedIn || _objects.LocalPlayer is null)
        {
            error = "You need to be logged in before RavaCast can place a screen.";
            return false;
        }

        if (viewportSize.X <= 1f || viewportSize.Y <= 1f)
        {
            error = "RavaCast could not read the game view size.";
            return false;
        }

        if (!TryReadViewProjectionMatrix(out var viewProjection) || !Matrix4x4.Invert(viewProjection, out var inverseViewProjection))
        {
            error = "RavaCast could not read the camera this frame.";
            return false;
        }

        var depthError = string.Empty;
        if (TrySampleSceneDepthAtScreenPoint(screenPoint, viewportSize, out var depth, out depthError)
            && TryResolveDepthWorldPoint(screenPoint, viewportSize, depth, inverseViewProjection, out var depthWorld, out depthError))
        {
            // The click path means exactly that: use the resolved world coordinate under the cursor.
            // Do not add half the screen height here. That made floor/wall clicks visibly drift away
            // from the actual picked depth point and made placement feel random.
            centre = depthWorld;
        }
        else if (TryBuildCameraRay(screenPoint, viewportSize, inverseViewProjection, out var rayOrigin, out var rayDirection)
            && TryIntersectHorizontalPlane(rayOrigin, rayDirection, ResolvePlacementHeight(preferredCentreY, screenHeight), out var planeWorld)
            && IsFinite(planeWorld))
        {
            centre = planeWorld;
            // Normal placement fallback; keep RavaCast logs quiet on healthy paths.
        }
        else
        {
            error = "RavaCast could not place the screen from that click. Try clicking the ground or another visible part of the game world.";
            return false;
        }

        if (!TryGetScreenYawFacingCameraOrPlayer(centre, out yaw))
            yaw = WrapRadians(_objects.LocalPlayer!.Rotation + MathF.PI);

        if (!IsFinite(centre) || !float.IsFinite(yaw))
        {
            error = "RavaCast could not use that screen position.";
            return false;
        }

        return true;
    }

    public unsafe bool TryProjectScreenPlaneToViewport(RavaCastPlane plane, Vector2 viewportSize, out Vector2 topLeft, out Vector2 topRight, out Vector2 bottomRight, out Vector2 bottomLeft, out string error)
    {
        topLeft = topRight = bottomRight = bottomLeft = Vector2.Zero;
        error = string.Empty;
        if (viewportSize.X <= 1f || viewportSize.Y <= 1f)
        {
            error = "RavaCast could not read the game view size.";
            return false;
        }

        if (!TryReadViewProjectionMatrix(out var viewProj))
        {
            error = "RavaCast could not read the camera this frame.";
            return false;
        }

        if (!TryProjectWorldToViewport(plane.TopLeft, viewProj, viewportSize, out topLeft)
            || !TryProjectWorldToViewport(plane.TopRight, viewProj, viewportSize, out topRight)
            || !TryProjectWorldToViewport(plane.BottomRight, viewProj, viewportSize, out bottomRight)
            || !TryProjectWorldToViewport(plane.BottomLeft, viewProj, viewportSize, out bottomLeft))
        {
            error = "Screen handles are off-screen or behind the camera.";
            return false;
        }

        return true;
    }

    private static unsafe bool TryReadViewProjectionMatrix(out Matrix4x4 viewProj)
    {
        viewProj = Matrix4x4.Identity;
        try
        {
            var control = GameControl.Instance();
            if (control is null) return false;

            var result = Matrix4x4.Identity;
            var src = (float*)&control->ViewProjectionMatrix;
            var dst = (float*)&result;
            for (var i = 0; i < 16; i++)
                dst[i] = src[i];

            viewProj = result;
            return true;
        }
        catch
        {
            viewProj = Matrix4x4.Identity;
            return false;
        }
    }

    private unsafe bool TrySampleSceneDepthAtScreenPoint(Vector2 screenPoint, Vector2 viewportSize, out float depth, out string error)
    {
        depth = 0f;
        error = string.Empty;

        try
        {
            var kernelDevice = GameKernelDevice.Instance();
            var renderTargets = GameRenderTargetManager.Instance();
            var depthStencil = renderTargets is not null ? renderTargets->DepthStencil : null;
            if (kernelDevice is null || kernelDevice->D3D11Forwarder == null || depthStencil is null || depthStencil->D3D11Texture2D == null)
            {
                error = "scene depth texture unavailable";
                return false;
            }

            var devicePtr = (IntPtr)kernelDevice->D3D11Forwarder;
            var depthPtr = (IntPtr)depthStencil->D3D11Texture2D;
            if (devicePtr == IntPtr.Zero || depthPtr == IntPtr.Zero)
            {
                error = "scene depth texture unavailable";
                return false;
            }

            Marshal.AddRef(devicePtr);
            using var d3dDevice = new SharpDX.Direct3D11.Device(devicePtr);
            using var context = d3dDevice.ImmediateContext;

            Marshal.AddRef(depthPtr);
            using var depthTexture = new SharpDX.Direct3D11.Texture2D(depthPtr);
            var desc = depthTexture.Description;
            if (desc.Width <= 1 || desc.Height <= 1)
            {
                error = "scene depth texture size invalid";
                return false;
            }

            if (desc.SampleDescription.Count > 1)
            {
                error = $"scene depth is multisampled ({desc.SampleDescription.Count}x)";
                return false;
            }

            var x = Math.Clamp((int)MathF.Round(screenPoint.X / Math.Max(1f, viewportSize.X) * (desc.Width - 1)), 0, desc.Width - 1);
            var y = Math.Clamp((int)MathF.Round(screenPoint.Y / Math.Max(1f, viewportSize.Y) * (desc.Height - 1)), 0, desc.Height - 1);
            var stagingDesc = new SharpDX.Direct3D11.Texture2DDescription
            {
                Width = 1,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                Usage = SharpDX.Direct3D11.ResourceUsage.Staging,
                BindFlags = SharpDX.Direct3D11.BindFlags.None,
                CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.Read,
                OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
            };

            using var staging = new SharpDX.Direct3D11.Texture2D(d3dDevice, stagingDesc);
            var region = new SharpDX.Direct3D11.ResourceRegion(x, y, 0, x + 1, y + 1, 1);
            context.CopySubresourceRegion(depthTexture, 0, region, staging, 0);
            var box = context.MapSubresource(staging, 0, SharpDX.Direct3D11.MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try
            {
                if (!TryReadDepthValue(desc.Format, box.DataPointer, out depth))
                {
                    error = "unsupported scene depth format: " + desc.Format;
                    return false;
                }
            }
            finally
            {
                context.UnmapSubresource(staging, 0);
            }

            if (!float.IsFinite(depth) || depth <= 0.000001f || depth >= 0.999999f)
            {
                error = $"scene depth at click was empty ({depth:0.000000})";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static unsafe bool TryReadDepthValue(SharpDX.DXGI.Format format, IntPtr ptr, out float depth)
    {
        depth = 0f;
        if (ptr == IntPtr.Zero) return false;

        switch (format)
        {
            case SharpDX.DXGI.Format.D32_Float:
            case SharpDX.DXGI.Format.R32_Float:
            case SharpDX.DXGI.Format.R32_Typeless:
                depth = *(float*)ptr;
                return true;
            case SharpDX.DXGI.Format.D24_UNorm_S8_UInt:
            case SharpDX.DXGI.Format.R24G8_Typeless:
                depth = (*(uint*)ptr & 0x00FFFFFFu) / 16777215f;
                return true;
            case SharpDX.DXGI.Format.D16_UNorm:
            case SharpDX.DXGI.Format.R16_UNorm:
            case SharpDX.DXGI.Format.R16_Typeless:
                depth = *(ushort*)ptr / 65535f;
                return true;
            default:
                return false;
        }
    }

    private static unsafe bool TryResolveDepthWorldPoint(Vector2 screenPoint, Vector2 viewportSize, float rawDepth, Matrix4x4 inverseViewProjection, out Vector3 world, out string error)
    {
        world = Vector3.Zero;
        error = string.Empty;

        var cameraKnown = TryGetCameraPosition(out var cameraPos);
        var bestScore = float.MaxValue;
        var best = Vector3.Zero;
        var found = false;

        // Some FFXIV/DX paths expose scene depth in the opposite direction to the clip depth we
        // need for unprojection. Test both raw and inverted depths and reject anything that lands
        // on/inside the camera, which was the cause of screens spawning at the camera position.
        Span<float> candidates = stackalloc float[2] { rawDepth, 1f - rawDepth };
        for (var i = 0; i < candidates.Length; i++)
        {
            var depth = Math.Clamp(candidates[i], 0.00001f, 0.99999f);
            if (!TryUnprojectScreenPoint(screenPoint, viewportSize, depth, inverseViewProjection, out var candidate) || !IsFinite(candidate))
                continue;

            var distance = cameraKnown ? Vector3.Distance(cameraPos, candidate) : 1f;
            if (!float.IsFinite(distance) || distance < 0.35f || distance > 120f)
                continue;

            // Prefer the point that reprojects closest to the original mouse coordinate, then prefer
            // saner distances. Reprojection is important because a bogus matrix/depth read can still
            // return a finite point that is nowhere near the clicked pixel.
            var reprojectionScore = 0f;
            if (TryReadViewProjectionMatrix(out var viewProj) && TryProjectWorldToViewport(candidate, viewProj, viewportSize, out var reproj))
                reprojectionScore = Vector2.DistanceSquared(reproj, screenPoint);
            else
                reprojectionScore = 256f;

            if (reprojectionScore > 4096f)
                continue;

            var score = reprojectionScore + MathF.Abs(distance - 4f) * 0.05f;
            if (score >= bestScore) continue;

            bestScore = score;
            best = candidate;
            found = true;
        }

        if (!found)
        {
            error = $"scene depth resolved only to the camera/invalid space ({rawDepth:0.000000})";
            return false;
        }

        world = best;
        return true;
    }

    private static bool TryUnprojectScreenPoint(Vector2 screenPoint, Vector2 viewportSize, float depth, Matrix4x4 inverseViewProjection, out Vector3 world)
    {
        world = Vector3.Zero;
        if (viewportSize.X <= 1f || viewportSize.Y <= 1f) return false;
        var ndcX = Math.Clamp((screenPoint.X / viewportSize.X) * 2f - 1f, -1f, 1f);
        var ndcY = Math.Clamp(1f - (screenPoint.Y / viewportSize.Y) * 2f, -1f, 1f);
        world = TransformClipPoint(new Vector3(ndcX, ndcY, Math.Clamp(depth, 0f, 1f)), inverseViewProjection);
        return IsFinite(world);
    }

    private static bool TryBuildCameraRay(Vector2 screenPoint, Vector2 viewportSize, Matrix4x4 inverseViewProjection, out Vector3 origin, out Vector3 direction)
    {
        origin = Vector3.Zero;
        direction = Vector3.UnitZ;
        if (viewportSize.X <= 1f || viewportSize.Y <= 1f) return false;

        var ndcX = Math.Clamp((screenPoint.X / viewportSize.X) * 2f - 1f, -1f, 1f);
        var ndcY = Math.Clamp(1f - (screenPoint.Y / viewportSize.Y) * 2f, -1f, 1f);
        var near = TransformClipPoint(new Vector3(ndcX, ndcY, 0f), inverseViewProjection);
        var far = TransformClipPoint(new Vector3(ndcX, ndcY, 1f), inverseViewProjection);
        var delta = far - near;
        if (!IsFinite(near) || !IsFinite(far) || delta.LengthSquared() <= 0.000001f) return false;

        origin = TryGetCameraPosition(out var cameraPos) ? cameraPos : near;
        direction = Vector3.Normalize(delta);
        return IsFinite(origin) && IsFinite(direction);
    }

    private static bool TryIntersectHorizontalPlane(Vector3 rayOrigin, Vector3 rayDirection, float y, out Vector3 world)
    {
        world = Vector3.Zero;
        if (Math.Abs(rayDirection.Y) <= 0.00001f) return false;
        var t = (y - rayOrigin.Y) / rayDirection.Y;
        if (!float.IsFinite(t) || t <= 0.05f || t > 100f) return false;
        world = rayOrigin + rayDirection * t;
        return IsFinite(world);
    }

    private float ResolvePlacementHeight(float preferredCentreY, float screenHeight)
    {
        if (float.IsFinite(preferredCentreY) && Math.Abs(preferredCentreY) > 0.001f)
            return preferredCentreY;
        var player = _objects.LocalPlayer;
        var height = Math.Clamp(screenHeight, 0.5f, 4.0f);
        return (player?.Position.Y ?? 0f) + height / 2f;
    }

    private bool TryGetScreenYawFacingCameraOrPlayer(Vector3 centre, out float yaw)
    {
        yaw = 0f;
        var target = Vector3.Zero;
        if (TryGetCameraPosition(out var cameraPos))
            target = cameraPos;
        else if (_objects.LocalPlayer is not null)
            target = _objects.LocalPlayer.Position;
        else
            return false;

        var normal = target - centre;
        normal.Y = 0f;
        if (!IsFinite(normal) || normal.LengthSquared() <= 0.0001f)
            return false;

        normal = Vector3.Normalize(normal);
        yaw = WrapRadians(MathF.Atan2(normal.X, normal.Z));
        return float.IsFinite(yaw);
    }

    private static bool TryProjectWorldToViewport(Vector3 world, Matrix4x4 viewProjection, Vector2 viewportSize, out Vector2 screen)
    {
        screen = Vector2.Zero;
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (!float.IsFinite(clip.W) || Math.Abs(clip.W) <= 0.000001f)
            return false;

        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        var ndcZ = clip.Z / clip.W;
        if (!float.IsFinite(ndcX) || !float.IsFinite(ndcY) || !float.IsFinite(ndcZ) || clip.W <= 0f)
            return false;

        screen = new Vector2(
            (ndcX + 1f) * 0.5f * viewportSize.X,
            (1f - ndcY) * 0.5f * viewportSize.Y);
        return IsFinite(new Vector3(screen.X, screen.Y, 0f));
    }

    private static Vector3 TransformClipPoint(Vector3 clip, Matrix4x4 inverseViewProjection)
    {
        var v = Vector4.Transform(new Vector4(clip, 1f), inverseViewProjection);
        if (Math.Abs(v.W) > 0.000001f)
            return new Vector3(v.X / v.W, v.Y / v.W, v.Z / v.W);
        return new Vector3(v.X, v.Y, v.Z);
    }

    private static unsafe bool TryGetCameraPosition(out Vector3 cameraPos)
    {
        cameraPos = Vector3.Zero;
        try
        {
            var camera = CameraManager.Instance()->CurrentCamera;
            if (camera is null) return false;
            cameraPos = new Vector3(camera->Position.X, camera->Position.Y, camera->Position.Z);
            return IsFinite(cameraPos);
        }
        catch
        {
            return false;
        }
    }

    internal static float WrapRadians(float radians)
    {
        while (radians > MathF.PI) radians -= MathF.Tau;
        while (radians < -MathF.PI) radians += MathF.Tau;
        return radians;
    }
}
