#!/usr/bin/env bash
set -euo pipefail

repo_dir=$(cd -- "$(dirname -- "$0")/.." && pwd)
cache=${TGDEBLOCKER_BUILD_DIR:-"$HOME/.cache/tgdeblocker-tdlib-mobile"}
output=${TGDEBLOCKER_NATIVE_OUTPUT:-"$repo_dir/artifacts/mobile-native/android"}
jobs=${TGDEBLOCKER_BUILD_JOBS:-4}
api=26
td_commit=42e6a5259551178d1dab54a22ad96d14bd906e20
openssl_version=3.6.3
ndk_version=28.2.13676358

if [[ $(uname -s) != Linux || $(uname -m) != x86_64 ]]; then
  printf '%s\n' 'Use Linux x86-64 or WSL 2 with a Linux NDK.' >&2
  exit 1
fi

if (( $# == 0 )); then set -- arm64-v8a; fi
for abi in "$@"; do
  case "$abi" in arm64-v8a|x86_64) ;; *) printf 'Unsupported ABI: %s\n' "$abi" >&2; exit 1 ;; esac
done

mkdir -p -- "$cache" "$output"
cache=$(cd -- "$cache" && pwd)
output=$(cd -- "$output" && pwd)

download() {
  local url=$1 path=$2 algorithm=$3 expected=$4
  if [[ ! -f $path ]]; then
    curl --fail --location --retry 2 --silent --show-error -o "$path.part" "$url"
    printf '%s  %s\n' "$expected" "$path.part" | "$algorithm" --check --status
    mv -- "$path.part" "$path"
  else
    printf '%s  %s\n' "$expected" "$path" | "$algorithm" --check --status
  fi
}

td_archive="$cache/td-$td_commit.tar.gz"
download "https://codeload.github.com/tdlib/td/tar.gz/$td_commit" "$td_archive" sha256sum \
  aaea97812f52c50aaa7aeec79f4ac534eb4413c10f91ffd7d3468b7cc5c7512f
openssl_archive="$cache/openssl-$openssl_version.tar.gz"
download "https://github.com/openssl/openssl/releases/download/openssl-$openssl_version/openssl-$openssl_version.tar.gz" \
  "$openssl_archive" sha256sum 243a86649cf6f23eeb6a2ff2456e09e5d77dd9018a54d3d96b0c6bdd6ba6c7f1

ndk=${ANDROID_NDK_HOME:-"$cache/android-ndk-r28c"}
if [[ ! -f $ndk/source.properties && -z ${ANDROID_NDK_HOME:-} ]]; then
  ndk_archive="$cache/android-ndk-r28c-linux.zip"
  download https://dl.google.com/android/repository/android-ndk-r28c-linux.zip "$ndk_archive" sha1sum \
    a7b54a5de87fecd125a17d54f73c446199e72a64
  unzip -oq "$ndk_archive" -d "$cache"
fi
grep -Fx "Pkg.Revision = $ndk_version" "$ndk/source.properties" >/dev/null
ndk=$(cd -- "$ndk" && pwd)
llvm_bin="$ndk/toolchains/llvm/prebuilt/linux-x86_64/bin"
test -x "$llvm_bin/clang"

td_src="$cache/td-$td_commit"
if [[ ! -f $td_src/CMakeLists.txt ]]; then tar -xzf "$td_archive" -C "$cache"; fi
cmake -S "$td_src" -B "$cache/td-generate" -G Ninja -DTD_GENERATE_SOURCE_FILES=ON -DCMAKE_BUILD_TYPE=Release
cmake --build "$cache/td-generate" --target prepare_cross_compiling --parallel "$jobs"

for abi in "$@"; do
  case "$abi" in arm64-v8a) openssl_target=android-arm64 ;; x86_64) openssl_target=android-x86_64 ;; esac
  openssl_src="$cache/openssl-src-$abi"
  openssl_prefix="$cache/openssl-install-$abi-api$api"
  if [[ ! -f $openssl_prefix/.built-$openssl_version ]]; then
    mkdir -p -- "$openssl_src"
    tar -xzf "$openssl_archive" -C "$openssl_src" --strip-components=1
    (
      cd -- "$openssl_src"
      export ANDROID_NDK_ROOT="$ndk" PATH="$llvm_bin:$PATH"
      ./Configure "$openssl_target" no-shared no-tests no-module -fPIC "-D__ANDROID_API__=$api" \
        --prefix="$openssl_prefix" --libdir=lib
      make -j"$jobs" build_libs
      make install_dev
    )
    touch "$openssl_prefix/.built-$openssl_version"
  fi

  build="$cache/td-build-$abi-api$api"
  cmake -S "$td_src" -B "$build" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI="$abi" -DANDROID_PLATFORM="android-$api" -DANDROID_STL=c++_static \
    -DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON -DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384 \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_CXX_FLAGS_RELEASE='-O2 -DNDEBUG' \
    -DOPENSSL_FOUND=TRUE -DOPENSSL_INCLUDE_DIR="$openssl_prefix/include" \
    -DOPENSSL_CRYPTO_LIBRARY="$openssl_prefix/lib/libcrypto.a" -DOPENSSL_SSL_LIBRARY="$openssl_prefix/lib/libssl.a" \
    "-DOPENSSL_LIBRARIES=$openssl_prefix/lib/libcrypto.a;$openssl_prefix/lib/libssl.a"
  cmake --build "$build" --target tdjson --parallel "$jobs"

  mkdir -p -- "$output/$abi"
  library="$output/$abi/libtdjson.so"
  "$llvm_bin/llvm-strip" --strip-debug --strip-unneeded "$build/libtdjson.so" -o "$library.part"
  "$llvm_bin/llvm-readelf" --program-headers --wide "$library.part" |
    awk '$1 == "LOAD" { if ($NF != "0x4000") bad=1; count++ } END { exit bad || !count }'
  "$llvm_bin/llvm-nm" --dynamic --defined-only "$library.part" | grep ' td_json_client_create$'
  mv -- "$library.part" "$library"
  sha256sum "$library"
done

cp -- "$td_src/LICENSE_1_0.txt" "$output/TDLib-LICENSE.txt"
cp -- "$cache/openssl-src-$abi/LICENSE.txt" "$output/OpenSSL-LICENSE.txt"
