#!/usr/bin/env bash
set -euo pipefail

if [[ $(uname -s) != Darwin ]]; then
  printf '%s\n' 'The iOS build requires macOS with Xcode and the iOS SDK.' >&2
  exit 1
fi

repo_dir=$(cd -- "$(dirname -- "$0")/.." && pwd)
cache=${TGDEBLOCKER_BUILD_DIR:-"$HOME/Library/Caches/tgdeblocker-tdlib-mobile"}
output=${TGDEBLOCKER_NATIVE_OUTPUT:-"$repo_dir/artifacts/mobile-native/ios"}
jobs=${TGDEBLOCKER_BUILD_JOBS:-4}
minimum_ios=15.0
td_commit=42e6a5259551178d1dab54a22ad96d14bd906e20
openssl_version=3.6.3

command -v cmake >/dev/null
command -v ninja >/dev/null
command -v gperf >/dev/null
xcodebuild -version
xcrun --sdk iphoneos --show-sdk-path >/dev/null
xcrun --sdk iphonesimulator --show-sdk-path >/dev/null

mkdir -p -- "$cache" "$output"
cache=$(cd -- "$cache" && pwd)
output=$(cd -- "$output" && pwd)
if [[ -e $output/TDLib.xcframework ]]; then
  printf 'Output already exists: %s\n' "$output/TDLib.xcframework" >&2
  exit 1
fi

download() {
  local url=$1 path=$2 sha=$3
  if [[ ! -f $path ]]; then
    curl --fail --location --retry 2 --silent --show-error -o "$path.part" "$url"
    printf '%s  %s\n' "$sha" "$path.part" | shasum -a 256 -c
    mv -- "$path.part" "$path"
  else
    printf '%s  %s\n' "$sha" "$path" | shasum -a 256 -c
  fi
}

td_archive="$cache/td-$td_commit.tar.gz"
download "https://codeload.github.com/tdlib/td/tar.gz/$td_commit" "$td_archive" \
  aaea97812f52c50aaa7aeec79f4ac534eb4413c10f91ffd7d3468b7cc5c7512f
openssl_archive="$cache/openssl-$openssl_version.tar.gz"
download "https://github.com/openssl/openssl/releases/download/openssl-$openssl_version/openssl-$openssl_version.tar.gz" \
  "$openssl_archive" 243a86649cf6f23eeb6a2ff2456e09e5d77dd9018a54d3d96b0c6bdd6ba6c7f1

td_src="$cache/td-$td_commit"
if [[ ! -f $td_src/CMakeLists.txt ]]; then tar -xzf "$td_archive" -C "$cache"; fi
cmake -S "$td_src" -B "$cache/td-generate" -G Ninja -DTD_GENERATE_SOURCE_FILES=ON -DCMAKE_BUILD_TYPE=Release
cmake --build "$cache/td-generate" --target prepare_cross_compiling --parallel "$jobs"

build_slice() {
  local slice=$1 sdk=$2 arch=$3 openssl_target=$4 version_flag=$5
  local openssl_src="$cache/openssl-src-$slice"
  local openssl_prefix="$cache/openssl-install-$slice-ios$minimum_ios"
  local build="$cache/td-build-$slice-ios$minimum_ios"
  local headers="$cache/td-headers-$slice"
  local sdk_path
  sdk_path=$(xcrun --sdk "$sdk" --show-sdk-path)
  test -f "$sdk_path/usr/include/zlib.h"
  test -f "$sdk_path/usr/lib/libz.tbd"
  if [[ ! -f $openssl_prefix/.built-$openssl_version ]]; then
    mkdir -p -- "$openssl_src"
    tar -xzf "$openssl_archive" -C "$openssl_src" --strip-components=1
    (
      cd -- "$openssl_src"
      ./Configure "$openssl_target" no-shared no-tests no-module "$version_flag" \
        --prefix="$openssl_prefix" --libdir=lib
      make -j"$jobs" build_libs
      make install_dev
    )
    touch "$openssl_prefix/.built-$openssl_version"
  fi

  cmake -S "$td_src" -B "$build" -G Ninja \
    -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT="$sdk_path" -DCMAKE_OSX_ARCHITECTURES="$arch" \
    -DCMAKE_OSX_DEPLOYMENT_TARGET="$minimum_ios" -DCMAKE_TRY_COMPILE_TARGET_TYPE=STATIC_LIBRARY \
    -DCMAKE_BUILD_TYPE=Release -DOPENSSL_USE_STATIC_LIBS=TRUE \
    -DOPENSSL_FOUND=TRUE -DOPENSSL_INCLUDE_DIR="$openssl_prefix/include" \
    -DOPENSSL_CRYPTO_LIBRARY="$openssl_prefix/lib/libcrypto.a" -DOPENSSL_SSL_LIBRARY="$openssl_prefix/lib/libssl.a" \
    -DZLIB_INCLUDE_DIR="$sdk_path/usr/include" -DZLIB_LIBRARY="$sdk_path/usr/lib/libz.tbd" \
    "-DOPENSSL_LIBRARIES=$openssl_prefix/lib/libcrypto.a;$openssl_prefix/lib/libssl.a"
  cmake --build "$build" --target tdjson_static --parallel "$jobs"

  local archive_list="$build/td-archives.txt"
  find "$build" -type f -name 'libtd*.a' ! -name 'libTDLib.a' -print | sort > "$archive_list"
  printf '%s\n' "$openssl_prefix/lib/libcrypto.a" "$openssl_prefix/lib/libssl.a" >> "$archive_list"
  xcrun libtool -static -o "$build/libTDLib.a" -filelist "$archive_list"
  local symbols="$build/tdlib-symbols.txt" symbol
  xcrun nm -gU "$build/libTDLib.a" > "$symbols"
  for symbol in create send receive execute destroy; do
    if ! grep -Eq "[[:space:]]_td_json_client_${symbol}$" "$symbols"; then
      printf 'Missing TDLib JSON symbol for %s: td_json_client_%s\n' "$slice" "$symbol" >&2
      return 1
    fi
  done

  # Force-load the same archive and system libraries used by the MAUI NativeReference.
  printf 'int main() { return 0; }\n' > "$build/tdlib-link-check.cpp"
  xcrun --sdk "$sdk" clang++ -arch "$arch" -isysroot "$sdk_path" "$version_flag" \
    "$build/tdlib-link-check.cpp" -Xlinker -force_load -Xlinker "$build/libTDLib.a" \
    -framework Foundation -framework Security -lz -o "$build/tdlib-link-check"

  mkdir -p -- "$headers/td/telegram"
  cp -- "$td_src/td/telegram/td_json_client.h" "$build/td/telegram/tdjson_export.h" "$headers/td/telegram/"
}

build_slice device iphoneos arm64 ios64-xcrun "-miphoneos-version-min=$minimum_ios"
build_slice simulator iphonesimulator arm64 iossimulator-arm64-xcrun "-mios-simulator-version-min=$minimum_ios"

device="$cache/td-build-device-ios$minimum_ios/libTDLib.a"
simulator="$cache/td-build-simulator-ios$minimum_ios/libTDLib.a"
if [[ ${TGDEBLOCKER_IOS_SIMULATOR_X64:-0} == 1 ]]; then
  build_slice simulator-x64 iphonesimulator x86_64 iossimulator-x86_64-xcrun "-mios-simulator-version-min=$minimum_ios"
  xcrun lipo -create "$simulator" "$cache/td-build-simulator-x64-ios$minimum_ios/libTDLib.a" \
    -output "$cache/libTDLib-universal-simulator.a"
  simulator="$cache/libTDLib-universal-simulator.a"
fi

xcodebuild -create-xcframework \
  -library "$device" -headers "$cache/td-headers-device" \
  -library "$simulator" -headers "$cache/td-headers-simulator" \
  -output "$output/TDLib.xcframework"
cp -- "$td_src/LICENSE_1_0.txt" "$output/TDLib-LICENSE.txt"
cp -- "$cache/openssl-src-device/LICENSE.txt" "$output/OpenSSL-LICENSE.txt"
