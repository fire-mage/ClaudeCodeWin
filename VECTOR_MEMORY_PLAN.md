# Vector Memory System — Implementation Plan

## Overview
Semantic search system for CCW using local ONNX embedding model + SQLite vector storage.
Provides Claude with relevant context from KB articles, chat history, notepad, and memory files.

## Architecture
```
User query → BPE Tokenizer → ONNX Embedding (float[384]) → Cosine Search in SQLite → Top-K results → Inject into Claude context
```

## NuGet Dependency
```xml
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.21.*" />
```

## Storage Locations
- ONNX model + tokenizer: `%LocalAppData%/ClaudeCodeWin/models/`
- Vector SQLite DBs: `%LocalAppData%/ClaudeCodeWin/vectors/{encoded-project-path}.db`
- NOT in %AppData% (OneDrive safety)

## Files to Create

### Phase 1: Foundation
1. **Models/VectorSearchResult.cs** — search result DTO (Id, SourceType, SourceId, Text, Similarity, Metadata)
2. **Services/EmbeddingModelManager.cs** — static class, download/validate/delete ONNX model (~82MB) + tokenizer.json (~780KB) from HuggingFace. Pattern: WhisperModelManager.
   - Model URL: `https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx`
   - Tokenizer URL: `https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/tokenizer.json`
3. **Models/AppSettings.cs** — add: VectorMemoryEnabled (bool, true), VectorMaxChatSessions (int, 50), VectorMinRelevance (float, 0.3f)

### Phase 2: Core Engine
4. **Services/BpeTokenizer.cs** — BPE tokenizer reading tokenizer.json. Encode(text, maxLength=256) → (InputIds, AttentionMask, TokenTypeIds)
5. **Services/VectorMemoryService.cs** — main service:
   - InitializeAsync() — load ONNX session (lazy, safe)
   - EmbedText(string) → float[384] (mean pooling + L2 normalize)
   - IndexDocumentAsync(projectPath, sourceType, sourceId, text, metadata) — chunk + embed + store in SQLite
   - Search(projectPath, query, topK, sourceTypeFilter) — brute-force cosine similarity
   - GetContextForQuery(projectPath, query, minRelevance) → formatted string for Claude
   - RebuildProjectIndexAsync(projectPath, documents) — full reindex
   - GetIndexStats(projectPath) → counts

### Phase 3: Integration
6. **App.xaml.cs** — create VectorMemoryService, pass to ViewModels/Services via setters
7. **ChatSessionViewModel** — inject context before sending to Claude; index assistant responses after completion
8. **KnowledgeBaseService** — index articles on save (SetVectorMemory setter)
9. **NotepadStorageService** — index notes on save
10. **SettingsWindow.xaml** — Vector Memory section (enable/disable, download/delete model, progress bar, stats, settings)

## SQLite Schema
```sql
CREATE TABLE documents (
    id TEXT PRIMARY KEY,
    source_type TEXT NOT NULL,
    source_id TEXT NOT NULL,
    chunk_index INTEGER DEFAULT 0,
    text TEXT NOT NULL,
    embedding BLOB NOT NULL,        -- float[384] as byte[1536]
    metadata TEXT,                   -- JSON
    created_at TEXT NOT NULL,
    model_ver TEXT NOT NULL
);
CREATE TABLE sync_state (
    source_type TEXT NOT NULL,
    source_id TEXT NOT NULL,
    last_hash TEXT NOT NULL,
    indexed_at TEXT NOT NULL,
    PRIMARY KEY (source_type, source_id)
);
```

## Edge Cases
- Model not downloaded → IsAvailable=false, all methods return empty, app works as before
- No internet → show error, retry button in Settings
- Low RAM (<500MB) → don't load model, log reason
- Corrupted model → catch InferenceSession exception → IsAvailable=false, offer redownload
- SQLite locked → WAL mode + retry with backoff
- Model version change → model_ver field mismatch → trigger reindex
- OneDrive conflict → impossible (everything in %LocalAppData%)

## ONNX Runtime Settings
```csharp
options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
options.IntraOpNumThreads = 2;
options.InterOpNumThreads = 1;
```

## Chunking Strategy
- ~200 words per chunk, 50-word overlap
- Min chunk: 50 chars (skip shorter)
- Max tokens per chunk: 256 (model limit)

## Context Injection Format
```xml
<vector-memory-context>
<!-- source: kb/article-id, relevance: 0.85 -->
Article text chunk here...

<!-- source: chat/session-id:42, relevance: 0.72 -->
Chat message text here...
</vector-memory-context>
```

## Implementation Order
Phase 1 → Phase 2 → Phase 3 (sequential, each depends on previous)

## Status
- ✅ Phase 1: Foundation — COMPLETE
- ✅ Phase 2: Core Engine — COMPLETE
- ✅ Phase 3: Integration — COMPLETE (all 10 items done)
