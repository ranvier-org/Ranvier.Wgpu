---
last_commit_released: de74dbbc0d4bfd5faf079ecc632e193047aea9bb
pre_release: alpha
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

## 0.1.0-alpha.1 - 2026-11-13

### 🚀 Features

* Initial Ranvier.Wgpu and Ranvier.Wgpu.Sdl engine and zero-copy WebCore bridge ([de74dbb](https://github.com/ranvier-org/Ranvier.Wgpu/commit/de74dbbc0d4bfd5faf079ecc632e193047aea9bb))

### 🔗 Dependencies

* `src/Ranvier.Wgpu` updated to 0.1.0-alpha.1
