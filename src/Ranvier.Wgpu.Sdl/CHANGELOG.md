---
last_commit_released: b6db66fe3bb8fc6da0991c3bd003e0579da691d8
depends_on:
  - ../Ranvier.Wgpu/
updaters:
  - regex:
      file: Ranvier.Wgpu.Sdl.fsproj
      pattern: (?<=<Version>)[^<]+(?=</Version>)
  - regex:
      file: README.md
      pattern: (?<=--version ).*
---

# Changelog

This changelog is generated from conventional commits by [EasyBuild.ShipIt](https://github.com/easybuild-org/EasyBuild.ShipIt).

## 0.2.1 - 2026-11-16

### 🐞 Bug Fixes

* *(deps)* Require FSharp.Core 10.0.100 and build against Ranvier.View.WebCore.Sdl 0.2.0 (#8) ([b6db66f](https://github.com/ranvier-org/Ranvier.Wgpu/commit/b6db66fe3bb8fc6da0991c3bd003e0579da691d8))

### 🔗 Dependencies

* `src/Ranvier.Wgpu` updated to 0.2.1

<strong><small>[View changes on Github](https://github.com/ranvier-org/Ranvier.Wgpu/compare/2f9b86b7544e87f8cb7adfd3410bfd4105655587..b6db66fe3bb8fc6da0991c3bd003e0579da691d8)</small></strong>

## 0.2.0 - 2026-11-15

### 🚀 Features

* *(wgpu)* Draw lines and discs under custom WGSL, add user uniforms, shader hooks and WGSL validation (#4) ([a764896](https://github.com/ranvier-org/Ranvier.Wgpu/commit/a7648963dbd93bb009e9853cd296c7b9d2ccd2c4))

### 🔗 Dependencies

* `src/Ranvier.Wgpu` updated to 0.2.0

<strong><small>[View changes on Github](https://github.com/ranvier-org/Ranvier.Wgpu/compare/9891fe191cca8716b31691bfc33afe66284fad67..2f9b86b7544e87f8cb7adfd3410bfd4105655587)</small></strong>

## 0.1.0 - 2026-11-14

### 🐞 Bug Fixes

* *(deps)* Build against the stable Ranvier cohort and release stable versions (#1) ([9891fe1](https://github.com/ranvier-org/Ranvier.Wgpu/commit/9891fe191cca8716b31691bfc33afe66284fad67))

### 🔗 Dependencies

* `src/Ranvier.Wgpu` updated to 0.1.0

<strong><small>[View changes on Github](https://github.com/ranvier-org/Ranvier.Wgpu/compare/eaa4328e7d956c65460b9bed9e16c04da2aa814d..9891fe191cca8716b31691bfc33afe66284fad67)</small></strong>

## 0.1.0-alpha.2 - 2026-11-13

### 🐞 Bug Fixes

* *(sdl)* Use owned Metal blit queue on macOS and pixel-verify element resize in tests ([eaa4328](https://github.com/ranvier-org/Ranvier.Wgpu/commit/eaa4328e7d956c65460b9bed9e16c04da2aa814d))

### 🔗 Dependencies

* `src/Ranvier.Wgpu` updated to 0.1.0-alpha.2

<strong><small>[View changes on Github](https://github.com/ranvier-org/Ranvier.Wgpu/compare/de74dbbc0d4bfd5faf079ecc632e193047aea9bb..eaa4328e7d956c65460b9bed9e16c04da2aa814d)</small></strong>

## 0.1.0-alpha.1 - 2026-11-13

### 🚀 Features

* Initial Ranvier.Wgpu and Ranvier.Wgpu.Sdl engine and zero-copy WebCore bridge ([de74dbb](https://github.com/ranvier-org/Ranvier.Wgpu/commit/de74dbbc0d4bfd5faf079ecc632e193047aea9bb))

### 🔗 Dependencies

* `src/Ranvier.Wgpu` updated to 0.1.0-alpha.1
