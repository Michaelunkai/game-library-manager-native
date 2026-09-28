using System;
using System.IO;
using System.Text.Json;

namespace GameLibrary.Native;

internal static class GameSaveOperationsFailureTests
{
    internal static void Run(string root)
    {
        string fixture = Path.Combine(root, "game-save-structured-failure");
        Directory.CreateDirectory(fixture);
        string executable = Path.Combine(fixture, "Fixture.exe");
        File.WriteAllText(executable, "fixture executable");
        const string errorCode = "UnauthorizedAccessException";
        foreach (var testCase in new[] { (Operation: "backup", Restore: false), (Operation: "restore", Restore: true) })
        {
            string errorMessage = "Fixture failure cause for " + testCase.Operation;
            string resultPath = Path.Combine(fixture, testCase.Operation + ".result.json");
            string operationLogPath = Path.Combine(fixture, testCase.Operation + ".log");
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                operation = testCase.Operation,
                selectedExecutable = executable,
                outcome = "failed",
                receiptDirectory = (string?)null,
                backupManifest = (string?)null,
                backupManifestSha256 = (string?)null,
                fileCount = 0,
                registryCount = 0,
                verifiedBytes = 0L,
                integrity = "failed",
                preRestoreRollback = (string?)null,
                errorCode,
                errorMessage
            }));

            try
            {
                using var ignored = GameSaveOperations.ReadStructuredResult(resultPath, executable,
                    restore: testCase.Restore, verifyOnly: false, exitCode: 1, operationLogPath: operationLogPath);
            }
            catch (IOException exception)
            {
                if (exception.Message.Contains(errorCode, StringComparison.Ordinal) &&
                    exception.Message.Contains(errorMessage, StringComparison.Ordinal) &&
                    exception.Message.Contains(operationLogPath, StringComparison.Ordinal)) continue;
                throw new InvalidOperationException(
                    "A schema-v1 " + testCase.Operation + " failure did not surface its structured error and operation log.", exception);
            }

            throw new InvalidOperationException("A failed " + testCase.Operation + " helper result was accepted as verified.");
        }

        string mismatchedResult = Path.Combine(fixture, "other-game.result.json");
        string otherExecutable = Path.Combine(fixture, "Other.exe");
        const string otherGameDetail = "Do not expose another game's failure";
        File.WriteAllText(mismatchedResult, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            operation = "backup",
            selectedExecutable = otherExecutable,
            outcome = "failed",
            integrity = "failed",
            errorCode,
            errorMessage = otherGameDetail
        }));
        try
        {
            using var ignored = GameSaveOperations.ReadStructuredResult(mismatchedResult, executable,
                restore: false, verifyOnly: false, exitCode: 1,
                operationLogPath: Path.Combine(fixture, "other-game.log"));
        }
        catch (IOException exception)
        {
            if (!exception.Message.Contains(errorCode, StringComparison.Ordinal) &&
                !exception.Message.Contains(otherGameDetail, StringComparison.Ordinal)) return;
            throw new InvalidOperationException("A failure receipt for a different executable leaked its details.", exception);
        }

        throw new InvalidOperationException("A failure receipt for a different executable was accepted.");
    }
}
