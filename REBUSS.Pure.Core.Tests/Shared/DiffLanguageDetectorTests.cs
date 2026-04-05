using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.Core.Tests.Shared;

public class DiffLanguageDetectorTests
{
    [Fact]
    public void Detect_CsFile_ReturnsCSharp()
    {
        var diff = "=== Services/OrderService.cs (modified: +5 -3) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.Equal(DiffLanguage.CSharp, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_TsFile_ReturnsTypeScript()
    {
        var diff = "=== src/app.ts (modified: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.Equal(DiffLanguage.TypeScript, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_TsxFile_ReturnsTypeScript()
    {
        var diff = "=== src/Component.tsx (modified: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.Equal(DiffLanguage.TypeScript, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_PyFile_ReturnsPython()
    {
        var diff = "=== scripts/main.py (modified: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.Equal(DiffLanguage.Python, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_GoFile_ReturnsGo()
    {
        var diff = "=== cmd/server.go (modified: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.Equal(DiffLanguage.Go, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_JavaFile_ReturnsJava()
    {
        var diff = "=== src/Main.java (modified: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.Equal(DiffLanguage.Java, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_RsFile_ReturnsRust()
    {
        var diff = "=== src/lib.rs (modified: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.Equal(DiffLanguage.Rust, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_UnknownExtension_ReturnsUnknown()
    {
        var diff = "=== data/config.yaml (modified: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.Equal(DiffLanguage.Unknown, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_NoFilePath_ReturnsUnknown()
    {
        Assert.Equal(DiffLanguage.Unknown, DiffLanguageDetector.Detect(""));
        Assert.Equal(DiffLanguage.Unknown, DiffLanguageDetector.Detect("no header here"));
    }

    [Fact]
    public void IsCSharp_CsFile_ReturnsTrue()
    {
        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.True(DiffLanguageDetector.IsCSharp(diff));
    }

    [Fact]
    public void IsCSharp_PyFile_ReturnsFalse()
    {
        var diff = "=== scripts/main.py (modified: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.False(DiffLanguageDetector.IsCSharp(diff));
    }

    [Fact]
    public void IsSkipped_SkippedFile_ReturnsTrue()
    {
        var diff = "=== huge.cs (modified: skipped) ===\nReason: full file rewrite";
        Assert.True(DiffLanguageDetector.IsSkipped(diff));
    }

    [Fact]
    public void IsSkipped_NormalFile_ReturnsFalse()
    {
        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 ===\n-old\n+new";
        Assert.False(DiffLanguageDetector.IsSkipped(diff));
    }

    [Fact]
    public void Detect_KtFile_ReturnsKotlin()
    {
        var diff = "=== src/Main.kt (modified: +1 -1) ===\n@@";
        Assert.Equal(DiffLanguage.Kotlin, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_KtsFile_ReturnsKotlin()
    {
        var diff = "=== build.gradle.kts (modified: +1 -1) ===\n@@";
        Assert.Equal(DiffLanguage.Kotlin, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_RbFile_ReturnsRuby()
    {
        var diff = "=== app/main.rb (modified: +1 -1) ===\n@@";
        Assert.Equal(DiffLanguage.Ruby, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_SwiftFile_ReturnsSwift()
    {
        var diff = "=== Sources/App.swift (modified: +1 -1) ===\n@@";
        Assert.Equal(DiffLanguage.Swift, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_PhpFile_ReturnsPhp()
    {
        var diff = "=== src/index.php (modified: +1 -1) ===\n@@";
        Assert.Equal(DiffLanguage.Php, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_CppFile_ReturnsCpp()
    {
        var diff = "=== src/main.cpp (modified: +1 -1) ===\n@@";
        Assert.Equal(DiffLanguage.Cpp, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_HFile_ReturnsCpp()
    {
        var diff = "=== include/types.h (modified: +1 -1) ===\n@@";
        Assert.Equal(DiffLanguage.Cpp, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_JsFile_ReturnsJavaScript()
    {
        var diff = "=== src/app.js (modified: +1 -1) ===\n@@";
        Assert.Equal(DiffLanguage.JavaScript, DiffLanguageDetector.Detect(diff));
    }

    [Fact]
    public void Detect_MjsFile_ReturnsJavaScript()
    {
        var diff = "=== src/utils.mjs (modified: +1 -1) ===\n@@";
        Assert.Equal(DiffLanguage.JavaScript, DiffLanguageDetector.Detect(diff));
    }
}
