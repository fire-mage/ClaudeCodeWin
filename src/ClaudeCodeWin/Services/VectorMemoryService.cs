using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeCodeWin.Models;
using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ClaudeCodeWin.Services;

/// <summary>
/// Semantic vector memory: embeds text using ONNX MiniLM model, stores in SQLite, searches by cosine similarity.
/// All methods are safe to call when the model is not loaded — they return empty results.
/// Thread-safe: ReaderWriterLockSlim for session access, lock for DB writes, SemaphoreSlim for init.
/// </summary>
public partial class VectorMemoryService : IDisposable
{
    private InferenceSession? _session;
    private BpeTokenizer? _tokenizer;
    private volatile bool _isAvailable;
    private int _disposed; // 0/1; thread-safe via Interlocked
    private readonly object _dbWriteLock = new();
    private readonly ReaderWriterLockSlim _sessionLock = new();  // CRITICAL #1: protects session from use-after-dispose
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private int _embedErrorLogged; // 0 = not logged; thread-safe via Interlocked
    private int _indexErrorCount; // consecutive indexing failures; disables after threshold with cooldown
    private long _indexDisabledUntilTicks; // UTC ticks; cooldown expiry for circuit breaker
    private int _writeCount; // tracks writes to throttle PruneOldChunks

    private const string ModelVersion = "minilm-v2";
    private const int EmbeddingDim = 384;
    private const int MaxTokens = 256;
    private const int ChunkWords = 200;
    private const int ChunkOverlapWords = 50;
    private const int MinChunkChars = 50;

    // Matches XML-like tags that could be mistaken for prompt structure (e.g. <system>, </instructions>)
    [System.Text.RegularExpressions.GeneratedRegex(@"</?[a-zA-Z][\w-]*(?:\s[^>]*)?>")]
    private static partial System.Text.RegularExpressions.Regex XmlTagPattern();

    private static readonly string VectorsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeWin", "vectors");

    public bool IsAvailable => _isAvailable;

    /// <summary>
    /// Initialize the ONNX session and tokenizer. Thread-safe, idempotent.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_disposed != 0) return;

        if (!await _initLock.WaitAsync(TimeSpan.FromSeconds(30))) return;
        try
        {
            if (_isAvailable || _disposed != 0) return;

            if (!EmbeddingModelManager.IsModelDownloaded()) return;
            if (!EmbeddingModelManager.HasSufficientMemory()) return;

            var tokenizer = BpeTokenizer.FromFile(EmbeddingModelManager.GetTokenizerPath());

            // ONNX Runtime copies SessionOptions internally; safe to dispose after session creation
            using var options = new SessionOptions();
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            options.InterOpNumThreads = 1;
            options.IntraOpNumThreads = 2;
            var session = new InferenceSession(EmbeddingModelManager.GetModelPath(), options);

            _sessionLock.EnterWriteLock();
            try
            {
                _tokenizer = tokenizer;
                _session = session;
                _isAvailable = true;
            }
            finally { _sessionLock.ExitWriteLock(); }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log("VECTOR_MEMORY_INIT_ERROR", ex.Message);
            _sessionLock.EnterWriteLock();
            try
            {
                _isAvailable = false;
                _session?.Dispose();
                _session = null;
                _tokenizer = null;
            }
            finally { _sessionLock.ExitWriteLock(); }
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Shutdown the ONNX session (releases file handles). Call before DeleteModel().
    /// </summary>
    public void Shutdown()
    {
        if (_disposed != 0) return;
        try { _sessionLock.EnterWriteLock(); }
        catch (ObjectDisposedException) { return; } // Dispose() raced ahead
        try
        {
            _isAvailable = false;
            _session?.Dispose();
            _session = null;
            _tokenizer = null;
        }
        finally { _sessionLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Embed text into a float[384] vector, L2-normalized. Returns null if not available.
    /// </summary>
    public float[]? EmbedText(string text)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(text)) return null;

        // CRITICAL #1: read lock prevents Dispose/Shutdown from destroying session mid-inference
        // Wrap lock acquisition in try-catch to handle race with Dispose() disposing the lock
        bool lockAcquired = false;
        try
        {
            _sessionLock.EnterReadLock();
            lockAcquired = true;
            if (_disposed != 0) return null;
            var session = _session;
            var tokenizer = _tokenizer;
            if (session == null || tokenizer == null || !_isAvailable) return null;

            var (inputIds, attentionMask, tokenTypeIds) = tokenizer.Encode(text, MaxTokens);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids",
                    new DenseTensor<long>(inputIds, [1, MaxTokens])),
                NamedOnnxValue.CreateFromTensor("attention_mask",
                    new DenseTensor<long>(attentionMask, [1, MaxTokens])),
                NamedOnnxValue.CreateFromTensor("token_type_ids",
                    new DenseTensor<long>(tokenTypeIds, [1, MaxTokens])),
            };

            using var results = session.Run(inputs);

            var output = results.First().AsTensor<float>();

            // Mean pooling with attention mask
            var embedding = new float[EmbeddingDim];
            float maskSum = 0;

            for (int t = 0; t < MaxTokens; t++)
            {
                if (attentionMask[t] == 0) continue;
                maskSum += 1;
                for (int d = 0; d < EmbeddingDim; d++)
                    embedding[d] += output[0, t, d];
            }

            if (maskSum > 0)
            {
                for (int d = 0; d < EmbeddingDim; d++)
                    embedding[d] /= maskSum;
            }

            // L2 normalize
            float norm = 0;
            for (int d = 0; d < EmbeddingDim; d++)
                norm += embedding[d] * embedding[d];
            norm = MathF.Sqrt(norm);

            if (norm > 0)
            {
                for (int d = 0; d < EmbeddingDim; d++)
                    embedding[d] /= norm;
            }

            return embedding;
        }
        catch (Exception ex)
        {
            if (Interlocked.CompareExchange(ref _embedErrorLogged, 1, 0) == 0)
                DiagnosticLogger.Log("VECTOR_EMBED_ERROR", ex.Message);
            return null;
        }
        finally
        {
            if (lockAcquired) _sessionLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Index a document. Splits long text into chunks, embeds each, stores in SQLite.
    /// Skips unchanged documents (hash-based dirty check inside transaction).
    /// </summary>
    public void IndexDocument(
        string projectPath,
        string sourceType,
        string sourceId,
        string text,
        Dictionary<string, string>? metadata = null)
    {
        if (!_isAvailable || string.IsNullOrWhiteSpace(text)) return;
        // Circuit breaker: 5 consecutive failures → 5-minute cooldown, then auto-retry
        if (Volatile.Read(ref _indexErrorCount) >= 5)
        {
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _indexDisabledUntilTicks)) return;
            Interlocked.Exchange(ref _indexErrorCount, 0); // cooldown expired — reset and retry
        }

        // Cap text to prevent runaway embedding for very large pastes (~10K words)
        const int MaxTextChars = 50_000;
        if (text.Length > MaxTextChars)
            text = text[..MaxTextChars];

        var dbPath = GetDbPath(projectPath);
        var hash = ComputeHash(text);

        var chunks = ChunkText(text);
        if (chunks.Count == 0) return;

        var metadataJson = metadata != null
            ? JsonSerializer.Serialize(metadata, Infrastructure.JsonDefaults.Options)
            : null;

        var now = DateTime.UtcNow.ToString("o");

        // Embed all chunks (CPU-bound — callers already offload to thread pool via Task.Run)
        var embeddings = new List<(int index, float[] vector)>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var vec = EmbedText(chunks[i]);
            if (vec != null)
                embeddings.Add((i, vec));
        }

        if (embeddings.Count == 0) return;

        // Hash check + write in single lock scope (no TOCTOU)
        try
        {
            lock (_dbWriteLock)
            {
                using var conn = OpenDb(dbPath);
                using var tx = conn.BeginTransaction();

                // Check hash inside transaction
                using (var hashCmd = conn.CreateCommand())
                {
                    hashCmd.CommandText = "SELECT last_hash FROM sync_state WHERE source_type = @st AND source_id = @si";
                    hashCmd.Parameters.AddWithValue("@st", sourceType);
                    hashCmd.Parameters.AddWithValue("@si", sourceId);
                    var existingHash = hashCmd.ExecuteScalar() as string;
                    if (existingHash == hash)
                    {
                        tx.Rollback();
                        return;
                    }
                }

                // Delete old chunks
                using (var delCmd = conn.CreateCommand())
                {
                    delCmd.CommandText = "DELETE FROM documents WHERE source_type = @st AND source_id = @si";
                    delCmd.Parameters.AddWithValue("@st", sourceType);
                    delCmd.Parameters.AddWithValue("@si", sourceId);
                    delCmd.ExecuteNonQuery();
                }

                // Insert new chunks
                foreach (var (index, vector) in embeddings)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"INSERT INTO documents (id, source_type, source_id, chunk_index, text, embedding, metadata, created_at, model_ver)
                        VALUES (@id, @st, @si, @ci, @txt, @emb, @meta, @ts, @mv)";
                    cmd.Parameters.AddWithValue("@id", $"{sourceType}:{sourceId}:{index}");
                    cmd.Parameters.AddWithValue("@st", sourceType);
                    cmd.Parameters.AddWithValue("@si", sourceId);
                    cmd.Parameters.AddWithValue("@ci", index);
                    cmd.Parameters.AddWithValue("@txt", chunks[index]);
                    cmd.Parameters.AddWithValue("@emb", VectorToBytes(vector));
                    cmd.Parameters.AddWithValue("@meta", (object?)metadataJson ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ts", now);
                    cmd.Parameters.AddWithValue("@mv", ModelVersion);
                    cmd.ExecuteNonQuery();
                }

                // Update sync state
                using (var syncCmd = conn.CreateCommand())
                {
                    syncCmd.CommandText = @"INSERT OR REPLACE INTO sync_state (source_type, source_id, last_hash, indexed_at)
                        VALUES (@st, @si, @hash, @ts)";
                    syncCmd.Parameters.AddWithValue("@st", sourceType);
                    syncCmd.Parameters.AddWithValue("@si", sourceId);
                    syncCmd.Parameters.AddWithValue("@hash", hash);
                    syncCmd.Parameters.AddWithValue("@ts", now);
                    syncCmd.ExecuteNonQuery();
                }

                tx.Commit();
                Interlocked.Exchange(ref _indexErrorCount, 0); // reset on success
            }

            // Prune oldest chunks periodically — outside _dbWriteLock to avoid blocking concurrent indexing
            if (Interlocked.Increment(ref _writeCount) % 50 == 0)
            {
                try
                {
                    lock (_dbWriteLock)
                    {
                        using var pruneConn = OpenDb(dbPath);
                        PruneOldChunks(pruneConn, maxChunks: 10000);
                    }
                }
                catch (Exception ex) { DiagnosticLogger.Log("VECTOR_PRUNE_TRIGGER_ERROR", ex.Message); }
            }
        }
        catch (Exception ex)
        {
            var errCount = Interlocked.Increment(ref _indexErrorCount);
            DiagnosticLogger.Log("VECTOR_INDEX_ERROR", $"[{errCount}/5] {ex.Message}");
            if (errCount >= 5)
            {
                Interlocked.Exchange(ref _indexDisabledUntilTicks, DateTime.UtcNow.AddMinutes(5).Ticks);
                DiagnosticLogger.Log("VECTOR_INDEX_DISABLED", "Indexing paused for 5 minutes after 5 consecutive failures");
            }
        }
    }

    /// <summary>
    /// Remove all indexed data for a document.
    /// </summary>
    public void RemoveDocument(string projectPath, string sourceType, string sourceId)
    {
        var dbPath = GetDbPath(projectPath);
        if (!File.Exists(dbPath)) return;

        lock (_dbWriteLock)
        {
            using var conn = OpenDb(dbPath);
            using var tx = conn.BeginTransaction();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM documents WHERE source_type = @st AND source_id = @si";
            cmd.Parameters.AddWithValue("@st", sourceType);
            cmd.Parameters.AddWithValue("@si", sourceId);
            cmd.ExecuteNonQuery();

            using var syncCmd = conn.CreateCommand();
            syncCmd.CommandText = "DELETE FROM sync_state WHERE source_type = @st AND source_id = @si";
            syncCmd.Parameters.AddWithValue("@st", sourceType);
            syncCmd.Parameters.AddWithValue("@si", sourceId);
            syncCmd.ExecuteNonQuery();

            tx.Commit();
        }
    }

    /// <summary>
    /// Search for similar documents by cosine similarity (brute-force).
    /// WARNING #1 fix: uses separate read-only connection, doesn't hold _dbWriteLock.
    /// </summary>
    public List<VectorSearchResult> Search(
        string projectPath,
        string query,
        int topK = 10,
        string? sourceTypeFilter = null)
    {
        if (!_isAvailable) return [];

        var queryVec = EmbedText(query);
        if (queryVec == null) return [];

        var dbPath = GetDbPath(projectPath);
        if (!File.Exists(dbPath)) return [];

        // Read-only connection — no _dbWriteLock needed (WAL allows concurrent readers)
        try
        {
            using var conn = OpenReadOnlyDb(dbPath);

            // Phase 1: scan only id + embedding for similarity
            var candidates = new List<(string id, float similarity)>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sourceTypeFilter != null
                    ? "SELECT id, embedding FROM documents WHERE source_type = @filter AND model_ver = @mv"
                    : "SELECT id, embedding FROM documents WHERE model_ver = @mv";

                if (sourceTypeFilter != null)
                    cmd.Parameters.AddWithValue("@filter", sourceTypeFilter);
                cmd.Parameters.AddWithValue("@mv", ModelVersion);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var embBytes = (byte[])reader["embedding"];
                    if (embBytes.Length != EmbeddingDim * sizeof(float)) continue;
                    var docVec = BytesToVector(embBytes);
                    var similarity = DotProduct(queryVec, docVec);
                    candidates.Add((reader.GetString(0), similarity));
                }
            }

            if (candidates.Count == 0) return [];

            var topIds = candidates
                .OrderByDescending(c => c.similarity)
                .Take(topK)
                .ToList();

            // Phase 2: fetch full data for top-K in single query
            var simMap = topIds.ToDictionary(t => t.id, t => t.similarity);
            var results = new List<VectorSearchResult>(topIds.Count);

            using var fetchCmd = conn.CreateCommand();
            var paramNames = new List<string>(topIds.Count);
            for (int i = 0; i < topIds.Count; i++)
            {
                var pName = $"@p{i}";
                paramNames.Add(pName);
                fetchCmd.Parameters.AddWithValue(pName, topIds[i].id);
            }
            fetchCmd.CommandText = $"SELECT id, source_type, source_id, text, metadata FROM documents WHERE id IN ({string.Join(',', paramNames)})";

            using var r = fetchCmd.ExecuteReader();
            while (r.Read())
            {
                var docId = r.GetString(0);
                results.Add(new VectorSearchResult
                {
                    Id = docId,
                    SourceType = r.GetString(1),
                    SourceId = r.GetString(2),
                    Text = r.GetString(3),
                    Similarity = simMap.GetValueOrDefault(docId),
                    Metadata = r.IsDBNull(4) ? null :
                        JsonSerializer.Deserialize<Dictionary<string, string>>(
                            r.GetString(4), Infrastructure.JsonDefaults.ReadOptions)
                });
            }

            // Re-sort by similarity (IN query doesn't preserve order)
            results.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
            return results;
        }
        catch (SqliteException)
        {
            return []; // DB may not have tables yet (race with first OpenDb)
        }
    }

    /// <summary>
    /// Build formatted context string for injection into Claude's prompt.
    /// WARNING #2 fix: escapes closing tags to prevent prompt injection.
    /// </summary>
    public string? GetContextForQuery(string projectPath, string query, float minRelevance = 0.3f)
    {
        var results = Search(projectPath, query, topK: 8);
        var relevant = results.Where(r => r.Similarity >= minRelevance).ToList();
        if (relevant.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("<vector-memory-context>");
        int totalChars = 0;
        const int MaxContextChars = 3000;
        foreach (var r in relevant)
        {
            if (totalChars + r.Text.Length > MaxContextChars) break;
            totalChars += r.Text.Length;
            var safeType = r.SourceType.Replace("<", "[").Replace(">", "]");
            var safeId = r.SourceId.Replace("<", "[").Replace(">", "]");
            sb.AppendLine($"<!-- source: {safeType}/{safeId}, relevance: {r.Similarity:F2} -->");
            // Sanitize all XML-like tags to prevent prompt structure injection
            var safeText = XmlTagPattern().Replace(r.Text, m => m.Value.Replace('<', '[').Replace('>', ']'));
            // Belt-and-suspenders: explicit escape for enclosing tag (regex may miss edge cases)
            safeText = safeText.Replace("</vector-memory-context>", "[/vector-memory-context]", StringComparison.OrdinalIgnoreCase);
            sb.AppendLine(safeText);
            sb.AppendLine();
        }
        sb.AppendLine("</vector-memory-context>");
        return totalChars > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// Get statistics about the vector index for a project.
    /// </summary>
    public (int TotalDocs, int TotalChunks, int KbCount, int ChatCount, int NoteCount) GetIndexStats(string projectPath)
    {
        var dbPath = GetDbPath(projectPath);
        if (!File.Exists(dbPath)) return (0, 0, 0, 0, 0);

        try
        {
            using var conn = OpenReadOnlyDb(dbPath);

            int totalChunks = ExecuteScalar(conn, "SELECT COUNT(*) FROM documents");
            int totalDocs = ExecuteScalar(conn, "SELECT COUNT(DISTINCT source_type || ':' || source_id) FROM documents");
            int kbCount = ExecuteScalar(conn, "SELECT COUNT(DISTINCT source_id) FROM documents WHERE source_type = 'kb'");
            // Count distinct sessions (source_id format: sessionId:role:ticks — extract sessionId before first ':')
            int chatCount = ExecuteScalar(conn, @"SELECT COUNT(DISTINCT CASE WHEN instr(source_id, ':') > 0
                THEN substr(source_id, 1, instr(source_id, ':') - 1) ELSE source_id END) FROM documents WHERE source_type = 'chat'");
            int noteCount = ExecuteScalar(conn, "SELECT COUNT(DISTINCT source_id) FROM documents WHERE source_type = 'notepad'");

            return (totalDocs, totalChunks, kbCount, chatCount, noteCount);
        }
        catch
        {
            return (0, 0, 0, 0, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Signal unavailable before acquiring lock — EmbedText short-circuits at volatile read
        _isAvailable = false;
        // Inline session cleanup (can't call Shutdown — it checks _disposed which is already 1)
        try { _sessionLock.EnterWriteLock(); }
        catch (ObjectDisposedException) { return; }
        try
        {
            _isAvailable = false;
            _session?.Dispose();
            _session = null;
            _tokenizer = null;
        }
        finally { _sessionLock.ExitWriteLock(); }
        _sessionLock.Dispose();
        _initLock.Dispose();
    }

    // ── Private helpers ──

    private static string GetDbPath(string projectPath)
    {
        Directory.CreateDirectory(VectorsDir);
        var encoded = EncodePath(projectPath);
        return Path.Combine(VectorsDir, encoded + ".db");
    }

    private static string EncodePath(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        var dirName = Path.GetFileName(Path.GetFullPath(path).TrimEnd('\\', '/')) ?? "root";
        var safeName = new string(dirName.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        if (safeName.Length > 30) safeName = safeName[..30];
        return $"{safeName}-{Convert.ToHexString(hash)[..16]}";
    }

    /// <summary>Open a read-write connection with schema init. Used for writes.</summary>
    private static SqliteConnection OpenDb(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath };
        var conn = new SqliteConnection(builder.ConnectionString);
        try
        {
            conn.Open();

            using (var walCmd = conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL";
                walCmd.ExecuteNonQuery();
            }
            using (var busyCmd = conn.CreateCommand())
            {
                busyCmd.CommandText = "PRAGMA busy_timeout=5000";
                busyCmd.ExecuteNonQuery();
            }

            // Always run — idempotent CREATE TABLE IF NOT EXISTS costs ~0.1ms
            using (var ddlCmd = conn.CreateCommand())
            {
                ddlCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS documents (
                        id TEXT PRIMARY KEY,
                        source_type TEXT NOT NULL,
                        source_id TEXT NOT NULL,
                        chunk_index INTEGER DEFAULT 0,
                        text TEXT NOT NULL,
                        embedding BLOB NOT NULL,
                        metadata TEXT,
                        created_at TEXT NOT NULL,
                        model_ver TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS sync_state (
                        source_type TEXT NOT NULL,
                        source_id TEXT NOT NULL,
                        last_hash TEXT NOT NULL,
                        indexed_at TEXT NOT NULL,
                        PRIMARY KEY (source_type, source_id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_docs_source ON documents(source_type, source_id);";
                ddlCmd.ExecuteNonQuery();
            }

            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Open a read-only connection for queries. No lock needed — WAL concurrent readers.</summary>
    private static SqliteConnection OpenReadOnlyDb(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        try { conn.Open(); return conn; }
        catch { conn.Dispose(); throw; }
    }

    /// <summary>Delete oldest chunks when the DB exceeds the cap.</summary>
    private static void PruneOldChunks(SqliteConnection conn, int maxChunks)
    {
        try
        {
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM documents";
            var count = (long)countCmd.ExecuteScalar()!;
            if (count <= maxChunks) return;

            using var tx = conn.BeginTransaction();

            // Find oldest documents (by source) to delete as whole units, with chunk counts
            using var findCmd = conn.CreateCommand();
            findCmd.CommandText = @"SELECT source_type, source_id, COUNT(*) as cnt FROM documents
                GROUP BY source_type, source_id ORDER BY MIN(created_at) ASC";
            var docsToRemove = new List<(string type, string id)>();
            long removedChunks = 0;
            var excess = count - maxChunks;

            using (var reader = findCmd.ExecuteReader())
            {
                while (reader.Read() && removedChunks < excess)
                {
                    docsToRemove.Add((reader.GetString(0), reader.GetString(1)));
                    removedChunks += reader.GetInt64(2);
                }
            }

            foreach (var (st, si) in docsToRemove)
            {
                using var delCmd = conn.CreateCommand();
                delCmd.CommandText = "DELETE FROM documents WHERE source_type = @st AND source_id = @si";
                delCmd.Parameters.AddWithValue("@st", st);
                delCmd.Parameters.AddWithValue("@si", si);
                delCmd.ExecuteNonQuery();

                using var syncCmd = conn.CreateCommand();
                syncCmd.CommandText = "DELETE FROM sync_state WHERE source_type = @st AND source_id = @si";
                syncCmd.Parameters.AddWithValue("@st", st);
                syncCmd.Parameters.AddWithValue("@si", si);
                syncCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (Exception ex) { DiagnosticLogger.Log("VECTOR_PRUNE_ERROR", ex.Message); }
    }

    private static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static List<string> ChunkText(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        if (words.Length <= ChunkWords)
        {
            var single = string.Join(' ', words);
            return single.Length >= MinChunkChars ? [single] : [];
        }

        var chunks = new List<string>();
        int start = 0;

        while (start < words.Length)
        {
            var end = Math.Min(start + ChunkWords, words.Length);
            var chunk = string.Join(' ', words[start..end]);
            if (chunk.Length >= MinChunkChars)
                chunks.Add(chunk);
            start += ChunkWords - ChunkOverlapWords;
        }

        return chunks;
    }

    private static byte[] VectorToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToVector(byte[] bytes)
    {
        if (bytes.Length != EmbeddingDim * sizeof(float))
            return new float[EmbeddingDim];
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private static float DotProduct(float[] a, float[] b)
    {
        int simdLen = Vector<float>.Count;
        int i = 0;
        float sum = 0;

        if (Vector.IsHardwareAccelerated && a.Length >= simdLen)
        {
            var vSum = Vector<float>.Zero;
            int limit = a.Length - (a.Length % simdLen);
            for (; i < limit; i += simdLen)
            {
                var va = new Vector<float>(a, i);
                var vb = new Vector<float>(b, i);
                vSum += va * vb;
            }
            sum = Vector.Dot(vSum, Vector<float>.One);
        }

        for (; i < a.Length; i++)
            sum += a[i] * b[i];

        return sum;
    }

    private static int ExecuteScalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
