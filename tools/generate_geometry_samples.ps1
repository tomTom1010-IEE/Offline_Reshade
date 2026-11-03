# Generates small synthetic test scenes with matching depth for UI and effect testing.
# Output: <name>-Color.png + <name>-Depth.rfloat (gallery naming); optionally a game-output pair
# coloroutput.png + depthoutput.rfloat for "Listen for game output", and grayscale depth previews.
# Depth is raw float32 little-endian, no header, rows bottom-to-top, reversed-Z device depth
# (near = 1, far/sky = 0), matching ReShade's default linearization (near 1, far plane 1000).
param(
    [Parameter(Mandatory)][string]$OutDir,
    [string]$LiveDir,
    [string]$PreviewDir
)
$ErrorActionPreference = 'Stop'
foreach ($dir in @($OutDir, $LiveDir, $PreviewDir) | Where-Object { $_ }) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public struct V3 {
  public double X, Y, Z;
  public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
  public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
  public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
  public static V3 operator *(V3 a, double s) { return new V3(a.X * s, a.Y * s, a.Z * s); }
  public static V3 Mul(V3 a, V3 b) { return new V3(a.X * b.X, a.Y * b.Y, a.Z * b.Z); }
  public static double Dot(V3 a, V3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
  public static V3 Cross(V3 a, V3 b) { return new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
  public V3 Norm() { double l = Math.Sqrt(X * X + Y * Y + Z * Z); return new V3(X / l, Y / l, Z / l); }
}

public abstract class Prim {
  public V3 Color; public double Spec;
  public abstract double Hit(V3 o, V3 d, out V3 n);
}
public class Sphere : Prim {
  public V3 C; public double R;
  public Sphere(double x, double y, double z, double r, double cr, double cg, double cb, double spec) { C = new V3(x, y, z); R = r; Color = new V3(cr, cg, cb); Spec = spec; }
  public override double Hit(V3 o, V3 d, out V3 n) {
    n = new V3(0, 0, 0);
    V3 oc = o - C; double b = V3.Dot(oc, d); double c = V3.Dot(oc, oc) - R * R; double h = b * b - c;
    if (h < 0) return -1; h = Math.Sqrt(h);
    double t = -b - h; if (t < 1e-4) return -1;
    n = ((o + d * t) - C).Norm(); return t;
  }
}
public class Box : Prim {
  public double[] Mn, Mx;
  public Box(double x0, double y0, double z0, double x1, double y1, double z1, double cr, double cg, double cb) { Mn = new[] { x0, y0, z0 }; Mx = new[] { x1, y1, z1 }; Color = new V3(cr, cg, cb); Spec = 0.1; }
  public override double Hit(V3 o, V3 d, out V3 n) {
    n = new V3(0, 0, 0);
    double[] oo = { o.X, o.Y, o.Z }, dd = { d.X, d.Y, d.Z };
    double tmin = -1e30, tmax = 1e30; int axis = 0; double sign = 0;
    for (int i = 0; i < 3; i++) {
      if (Math.Abs(dd[i]) < 1e-12) { if (oo[i] < Mn[i] || oo[i] > Mx[i]) return -1; continue; }
      double t1 = (Mn[i] - oo[i]) / dd[i], t2 = (Mx[i] - oo[i]) / dd[i], s = -1;
      if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; s = 1; }
      if (t1 > tmin) { tmin = t1; axis = i; sign = s; }
      if (t2 < tmax) tmax = t2;
      if (tmin > tmax) return -1;
    }
    if (tmin < 1e-4) return -1;
    double[] nn = { 0, 0, 0 }; nn[axis] = sign; n = new V3(nn[0], nn[1], nn[2]);
    return tmin;
  }
}
public class Floor : Prim {
  public Floor() { Color = new V3(0.8, 0.8, 0.78); Spec = 0; }
  public override double Hit(V3 o, V3 d, out V3 n) {
    n = new V3(0, 1, 0);
    if (Math.Abs(d.Y) < 1e-9) return -1;
    double t = -o.Y / d.Y; return t > 1e-4 ? t : -1;
  }
}

public static class GeoGen {
  const double Near = 1.0, Far = 1000.0;

  static V3 Sky(V3 d) {
    double k = Math.Max(0, Math.Min(1, d.Y * 2.5));
    return new V3(0.78, 0.84, 0.92) * (1 - k) + new V3(0.36, 0.56, 0.86) * k;
  }
  static double Trace(List<Prim> prims, V3 o, V3 d, out Prim hit, out V3 n) {
    double best = 1e30; hit = null; n = new V3(0, 1, 0);
    foreach (var p in prims) { V3 pn; double t = p.Hit(o, d, out pn); if (t > 0 && t < best) { best = t; hit = p; n = pn; } }
    return best;
  }
  static float DeviceDepth(double z) {
    if (z <= Near) return 1f;
    double nonReversed = Far * (z - Near) / (z * (Far - Near));
    return (float)Math.Max(0, Math.Min(1, 1 - nonReversed));
  }

  public static void Render(string colorPath, string depthPath, string previewPath, int w, int h,
                            V3 cam, V3 target, double fovDeg, List<Prim> prims, V3 sunDir) {
    V3 f = (target - cam).Norm(); V3 r = V3.Cross(new V3(0, 1, 0), f).Norm(); V3 u = V3.Cross(f, r);
    V3 toSun = (sunDir * -1).Norm();
    double th = Math.Tan(fovDeg * Math.PI / 360.0), aspect = (double)w / h;
    var rgb = new byte[w * h * 3]; var depth = new float[w * h]; var vis = new byte[w * h * 3];
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
      V3 d = (f + r * ((2 * (x + 0.5) / w - 1) * aspect * th) + u * ((1 - 2 * (y + 0.5) / h) * th)).Norm();
      Prim hit; V3 n; double t = Trace(prims, cam, d, out hit, out n);
      V3 col; float dev; double lin;
      if (hit == null) { col = Sky(d); dev = 0f; lin = 1; }
      else {
        V3 p = cam + d * t; double z = V3.Dot(p - cam, f);
        dev = DeviceDepth(z); lin = Math.Min(1, (z - Near) / (Far - Near));
        V3 baseCol = hit.Color;
        if (hit is Floor) {
          bool odd = ((int)Math.Floor(p.X) + (int)Math.Floor(p.Z) & 1) != 0;
          V3 check = odd ? new V3(0.82, 0.82, 0.8) : new V3(0.34, 0.35, 0.38);
          double fade = Math.Max(0, Math.Min(1, (z - 12) / 30));
          baseCol = check * (1 - fade) + new V3(0.58, 0.585, 0.59) * fade;
        }
        Prim blocker; V3 bn; Trace(prims, p + n * 1e-3, toSun, out blocker, out bn);
        double ndl = Math.Max(0, V3.Dot(n, toSun)) * (blocker == null ? 1 : 0.12);
        double ambient = 0.22 + 0.16 * (0.5 + 0.5 * n.Y);
        col = V3.Mul(baseCol, new V3(1, 1, 1) * ambient + new V3(1.0, 0.95, 0.86) * (0.95 * ndl));
        if (hit.Spec > 0 && ndl > 0) { V3 hv = (toSun - d).Norm(); double s = Math.Pow(Math.Max(0, V3.Dot(n, hv)), 48) * hit.Spec; col = col + new V3(s, s, s); }
        double haze = 1 - Math.Exp(-z * 0.006);
        col = col * (1 - haze) + Sky(new V3(0, 0.05, 1)) * haze;
      }
      int i = (y * w + x) * 3;
      rgb[i] = Enc(col.Z); rgb[i + 1] = Enc(col.Y); rgb[i + 2] = Enc(col.X);  // BGR for GDI+
      depth[(h - 1 - y) * w + x] = dev;                                     // file rows bottom-to-top
      byte g = (byte)Math.Max(0, Math.Min(255, 255 - Math.Sqrt(lin * 25) * 255)); vis[i] = vis[i + 1] = vis[i + 2] = g;
    }
    Save(colorPath, rgb, w, h);
    if (!string.IsNullOrEmpty(previewPath)) Save(previewPath, vis, w, h);
    using (var bw = new BinaryWriter(File.Create(depthPath))) foreach (var v in depth) bw.Write(v);
  }
  static byte Enc(double c) { return (byte)Math.Max(0, Math.Min(255, Math.Round(Math.Pow(Math.Max(0, Math.Min(1, c)), 1 / 2.2) * 255))); }
  static void Save(string path, byte[] bgr, int w, int h) {
    using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb)) {
      var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
      for (int y = 0; y < h; y++) Marshal.Copy(bgr, y * w * 3, data.Scan0 + y * data.Stride, w * 3);
      bmp.UnlockBits(data); bmp.Save(path, ImageFormat.Png);
    }
  }
}
'@

$sun = [V3]::new(-0.5, -1.0, 0.55)
function New-Scene([string]$name, [int]$w, [int]$h, [double[]]$cam, [double[]]$look, [double]$fov, $prims) {
  $list = New-Object 'System.Collections.Generic.List[Prim]'
  $list.Add([Floor]::new()); foreach ($p in $prims) { $list.Add($p) }
  $preview = if ($PreviewDir) { Join-Path $PreviewDir "$name-depthvis.png" } else { $null }
  [GeoGen]::Render((Join-Path $OutDir "$name-Color.png"), (Join-Path $OutDir "$name-Depth.rfloat"), $preview,
    $w, $h, [V3]::new($cam[0], $cam[1], $cam[2]), [V3]::new($look[0], $look[1], $look[2]), $fov, $list, $sun)
  "$name ${w}x$h"
}

# Geo01: mixed primitives at mid distance (AO / GI / DOF).
New-Scene 'Geo01' 960 540 @(0, 1.6, 0) @(0, 0.8, 8) 50 @(
  [Sphere]::new(-1.4, 0.8, 5.5, 0.8, 0.85, 0.25, 0.2, 0.5), [Sphere]::new(0.9, 0.45, 4.2, 0.45, 0.2, 0.4, 0.9, 0.6),
  [Sphere]::new(0.2, 1.3, 10, 1.3, 0.9, 0.9, 0.85, 0.3), [Sphere]::new(-6, 3, 30, 3, 0.6, 0.5, 0.8, 0.2),
  [Box]::new(1.8, 0, 6.5, 3.2, 1.4, 7.9, 0.95, 0.75, 0.2), [Box]::new(-3.6, 0, 9, -2.4, 2.6, 10.2, 0.3, 0.75, 0.4))
# Geo02: colonnade receding to the horizon (long depth range, fog, DOF falloff).
$pillars = foreach ($i in 0..9) { $z = 4 + $i * 5; $c = if ($i % 2) { @(0.85, 0.82, 0.75) } else { @(0.55, 0.62, 0.8) }
  [Box]::new(-2.5, 0, $z - 0.3, -1.9, 3.2, $z + 0.3, $c[0], $c[1], $c[2]); [Box]::new(1.9, 0, $z - 0.3, 2.5, 3.2, $z + 0.3, $c[0], $c[1], $c[2]) }
New-Scene 'Geo02' 960 540 @(0, 1.5, 0) @(0, 1.2, 20) 55 (@($pillars) + @([Sphere]::new(0.5, 0.5, 3, 0.5, 0.95, 0.55, 0.15, 0.6), [Sphere]::new(0, 2, 60, 2, 0.9, 0.9, 0.9, 0.2)))
# Geo03: very near object against open sky (near-depth edge cases, focus pulling).
$row = foreach ($i in 0..5) { $z = @(3, 5, 8, 12, 18, 26)[$i]; [Sphere]::new(0.8 + 0.25 * $i, 0.25, $z, 0.25, 0.3 + 0.12 * $i, 0.8 - 0.1 * $i, 0.4, 0.5) }
New-Scene 'Geo03' 960 540 @(0, 1.0, 0) @(0, 0.9, 5) 45 (@([Sphere]::new(-0.6, 0.9, 2.2, 0.55, 0.15, 0.7, 0.7, 0.7), [Box]::new(0.4, 0, 4.5, 1.4, 1.0, 5.5, 0.95, 0.45, 0.4)) + @($row))
# Geo04: portrait tower (different aspect ratio and size switching).
New-Scene 'Geo04' 540 960 @(0, 2.2, -0.5) @(0, 1.8, 7) 60 @(
  [Box]::new(-0.6, 0, 6, 0.6, 1.2, 7.2, 0.8, 0.35, 0.3), [Box]::new(-0.45, 1.2, 6.15, 0.45, 2.1, 7.05, 0.35, 0.6, 0.85),
  [Box]::new(-0.3, 2.1, 6.3, 0.3, 2.8, 6.9, 0.95, 0.8, 0.3), [Sphere]::new(0, 3.15, 6.6, 0.35, 0.9, 0.9, 0.92, 0.6),
  [Sphere]::new(-1.6, 0.5, 4.5, 0.5, 0.3, 0.75, 0.45, 0.5), [Sphere]::new(1.5, 0.7, 9, 0.7, 0.7, 0.4, 0.8, 0.4))

if ($LiveDir) {
  Copy-Item (Join-Path $OutDir 'Geo01-Color.png') (Join-Path $LiveDir 'coloroutput.png') -Force
  Copy-Item (Join-Path $OutDir 'Geo01-Depth.rfloat') (Join-Path $LiveDir 'depthoutput.rfloat') -Force
}
