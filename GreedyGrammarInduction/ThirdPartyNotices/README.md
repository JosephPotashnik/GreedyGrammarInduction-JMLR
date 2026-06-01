# Third-party native runtime notices

This application vendors the MSYS2 MinGW-w64 runtime DLLs needed by
`nauty_wrapper.dll` so the application can run on Windows without requiring
users to install MSYS2, MinGW, or GCC separately.

## MSYS2 gcc-libs

- Package: `mingw-w64-x86_64-gcc-libs`
- Repository: `mingw64`
- Version: `15.2.0-14`
- Source package URL: <https://repo.msys2.org/mingw/mingw64/mingw-w64-x86_64-gcc-libs-15.2.0-14-any.pkg.tar.zst>
- Package SHA256: `717F699E690374360764A083EB5CEEEF495B54A22ED81D52CB5C714AAE3FBAF8`
- Vendored file: `libgcc_s_seh-1.dll`
- Vendored file SHA256: `12BC032183350643C3D6ABFE823FF416AC721463AFB48DEC71A6E37844A7432A`

The package license notice is included in `MSYS2-gcc-libs/`.

## MSYS2 libwinpthread

- Package: `mingw-w64-x86_64-libwinpthread`
- Repository: `mingw64`
- Version: `14.0.0.r14.g4761eabdd-1`
- Source package URL: <https://repo.msys2.org/mingw/mingw64/mingw-w64-x86_64-libwinpthread-14.0.0.r14.g4761eabdd-1-any.pkg.tar.zst>
- Package SHA256: `1A3A0D7BB0DA45BF43F7F7A4012782331404EC5BF69DBDFA3C306C6055EE2E96`
- Vendored file: `libwinpthread-1.dll`
- Vendored file SHA256: `851F61482AD5B6AAC7C6ABC54BBE31D24F89E0CA683A75FCEC2D47F86B2D2242`

The package license notice is included in `MSYS2-libwinpthread/`.
