using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Bpcg.Core;
using Xunit;

namespace Bpcg.Tests;

/// <summary>
/// 출력 지문 시험. tiny 프로필·시드 0 으로 전체 단계(행성 → 히어로 → 회랑 → 지구본)를 돌리고,
/// 출력 파일마다 SHA-256 을 구해 커밋해 둔 값(tests/fixtures/run_digest_tiny_seed0.json)과 비교합니다.
/// 코드를 고친 뒤 출력이 한 바이트라도 바뀌면 이 시험이 바뀐 파일 이름을 알려 줍니다.
/// 출력을 일부러 바꾼 변경이면 BPCG_UPDATE_DIGEST=1 로 한 번 돌려 지문 파일을 다시 쓰고, 그 파일을 함께 커밋합니다.
/// </summary>
public sealed class RunDigestTests
{
    // 숫자 배열을 그대로 담은 파일만 봅니다. JSON 은 걸린 시간·커밋 해시가 들어 있고, PNG·npz 는 압축 라이브러리 판에 따라 바이트가 달라집니다.
    private static readonly string[] Extensions = [".npy", ".bin", ".u8", ".glb"];

    private static string DigestPath => Path.Combine(Paths.Root, "tests", "fixtures", "run_digest_tiny_seed0.json");

    private static SortedDictionary<string, string> HashRun(string runDir)
    {
        var digest = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(runDir, "*", SearchOption.AllDirectories))
        {
            if (!Extensions.Contains(Path.GetExtension(path), StringComparer.Ordinal))
            {
                continue;
            }
            string name = Path.GetRelativePath(runDir, path).Replace('\\', '/');
            digest[name] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }
        return digest;
    }

    [Fact]
    public void TinyRunOutputMatchesCommittedDigest()
    {
        // 초월 함수(exp, log, pow)가 운영체제의 수학 라이브러리를 따라 마지막 자리가 달라지므로, 지문은 한 플랫폼에서만 맞습니다.
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Assert.Skip("출력 지문은 macOS arm64 에서 만든 값이라 다른 플랫폼에서는 건너뜁니다 (golden 대조 시험과 같은 까닭)");
        }
        string runDir = Path.Combine(Path.GetTempPath(), "bpcg-digest-" + Guid.NewGuid());
        try
        {
            Config cfg = Config.LoadConfig("earth", "tiny");
            Runs.All(cfg, runDir, flat: false, engineDir: null, _ => { });
            SortedDictionary<string, string> actual = HashRun(runDir);
            Assert.True(actual.Count > 20, $"출력 파일이 {actual.Count}개뿐입니다. 전체 단계가 끝까지 돌지 않았습니다");

            if (Environment.GetEnvironmentVariable("BPCG_UPDATE_DIGEST") == "1")
            {
                File.WriteAllText(DigestPath, JsonSerializer.Serialize(actual, JsonOptions) + "\n");
                return;
            }

            Assert.True(File.Exists(DigestPath), $"지문 파일이 없습니다: {DigestPath}. BPCG_UPDATE_DIGEST=1 로 한 번 돌려 만드세요");
            Dictionary<string, string> expected =
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(DigestPath))!;
            var changed = actual.Where(kv => expected.TryGetValue(kv.Key, out string? h) && h != kv.Value).Select(kv => kv.Key).ToList();
            var added = actual.Keys.Where(k => !expected.ContainsKey(k)).ToList();
            var removed = expected.Keys.Where(k => !actual.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
            Assert.True(
                changed.Count == 0 && added.Count == 0 && removed.Count == 0,
                $"tiny 프로필·시드 0 의 출력이 커밋해 둔 지문과 다릅니다. 내용이 바뀐 파일 {changed.Count}개 ({Join(changed)}), "
                + $"새로 생긴 파일 {added.Count}개 ({Join(added)}), 없어진 파일 {removed.Count}개 ({Join(removed)}). "
                + "출력을 바꾸려던 변경이 아니면 버그입니다. 일부러 바꿨다면 BPCG_UPDATE_DIGEST=1 dotnet test --solution Bpcg.slnx 로 "
                + "tests/fixtures/run_digest_tiny_seed0.json 을 다시 쓰고 함께 커밋하세요");
        }
        finally
        {
            if (Directory.Exists(runDir))
            {
                Directory.Delete(runDir, true);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Join(List<string> names) =>
        names.Count == 0 ? "없음" : string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", …" : "");
}
