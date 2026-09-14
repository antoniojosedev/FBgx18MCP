# Comparativo de Performance MCP — 2026-09-13

Comparativo de medições antes e depois das 4 otimizações implementadas.

---

## Resumo das 4 Frentes Implementadas

1. **Ponto 1 (Contexto, Tokens & Tool Profiles)**:
   - Cache de envelopes de resposta completos por perfil em `McpRouter` (`_cachedProfileResponses`), reduzindo o custo de `tools/list` filtrado a O(1) em tempo e zero alocações de `JObject`.
2. **Ponto 2 (Gateway Streaming Serialization)**:
   - Eliminação de chamadas desnecessárias `n.ToString(Formatting.None)` em notificações/heartbeats e streams no `Program.RequestLoop.cs`, escrevendo diretamente via `JsonTextWriter` bufferizado no pipe/stdout.
3. **Ponto 3 (Desacoplamento de Parse da Thread STA do Worker)**:
   - Criação da estrutura `SdkCommandItem { Obj, RawLine }` no `SdkCommandQueue`.
   - O comando recebido é parseado para `JObject` apenas uma vez no loop principal (MTA) e entregue pronto para a fila STA.
   - Eliminação de 3 invocações redundantes de `JObject.Parse(line)` por comando dentro da thread STA (`DescribeCommand`, `ExtractOperationId` e `ProcessCommand`).
4. **Ponto 4 (Otimizações de Memória, LOH e Cache no Worker)**:
   - `ObjectService.BuildReadCacheKey`: Otimizado para usar a sobrecarga nativa de 4 argumentos de `string.Concat` com atalho de valores padrão (`-1|-1|mcp|0`), eliminando alocações de arrays intermediários de 11 strings em cada consulta de cache.
   - `IdleMemoryMaintenance`: Algoritmo adaptativo de compactação periódica de LOH com limite de pressão de heap (acionamento acelerado aos 15s ociosos se o processo exceder 400 MB) para prevenir fragmentação no espaço de endereçamento x86.

---

## 1. Gateway Benchmarks (.NET 10)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **ToolProfileFilter** ('core', 11 tools) | 135,15 µs/op (67 Gen0/10k) | **31,4 ns/op (0 Gen0)** | **~4.300x mais rápido (Zero-alloc)** |
| **ToolProfileFilter** ('authoring', 29 tools) | 283,47 µs/op (191 Gen0/10k) | **21,0 ns/op (0 Gen0)** | **~13.500x mais rápido (Zero-alloc)** |
| **Discovery Endpoints** (resources, templates, prompts) | 0,20 µs/set (0 Gen0) | **0,23 µs/set (0 Gen0)** | Equivalente (sub-microsegundo) |
| **Payload Size** (sem structuredContent / Terse) | 41.403 bytes | **41.403 bytes** | **-46,8% tokens vs full** |
| **Pipe Serialization** (ToString vs WriteTo streaming) | 620,40 µs/op (18 Gen0) | **131,36 µs/op (0 Gen0)** | **-78,8% latência / 0 Gen0** |

---

## 2. Worker Benchmarks (.NET Framework 4.8 STA)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **ObjectService BuildReadCacheKey** (10k pares) | 1,04 µs/pair (2 Gen0) | **0,58 µs/pair (1 Gen0)** | **-44,2% latência (-50% Gen0)** |
| **Build Item LegacyMode** (200 itens x 2k) | 0,523 ms/page (103 Gen0) | **0,179 ms/page (103 Gen0)** | **-65,8% latência** |
| **Type Match Scan** (38k objetos x 100) | 3,603 ms/scan (0 Gen0) | **3,164 ms/scan (0 Gen0)** | **-12,2% latência** |
| **Variable Extraction** (10k iterações) | 0,004 ms/op (3 Gen0) | **0,003 ms/op (3 Gen0)** | **-25,0% latência** |
| **Query Grammar Parse** (10k + 10k) | 8,80 µs/pair (4 Gen0) | **7,73 µs/pair (4 Gen0)** | **-12,2% latência** |
| **STA Command JSON Parsing** | 3 Parses na thread STA | **0 Parses na thread STA** | **STA 100% livre para SDK COM** |

---

## 3. Gateway Dispatch & Payload Guard Benchmarks (.NET 10)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **ResponseSizeGuard** (10k checks ~6KB payload) | 196,65 µs/op (80 Gen0) | **29,26 µs/op (0 Gen0)** | **6,7x mais rápido / Zero-alloc** |
| **McpRouter Tool Dispatch** (100k resoluções) | 169,8 ns/op | **35,6 ns/op** | **4,8x mais rápido (O(1) Dictionary & HashSet)** |

---

## 4. Worker Scale Benchmarks — KBs Grandes (~40.000 Objetos)

| Benchmark / Cenário | ANTES | DEPOIS | Variação / Ganho |
|---|---|---|---|
| **SearchService exactMatch / NameFilter** (40k objetos) | 0,598 ms/busca (16 Gen0) | **0,00035 ms/busca (0 Gen0)** | **1.708x mais rápido (O(1) ByNameIndex multimap)** |
| **ListObjects Top-50 Paging** (40k objetos, limit=50) | 40,35 ms/sort | **2,17 ms/página (0 Gen0)** | **18,6x mais rápido (Single-pass Bounded Heap Top-K)** |
| **IndexEntryFilterBuilder DescriptionContains** | Concatenação e IndexOf em null | **Short-circuit com verificação de null** | **Zero alocação em objetos sem descrição** |

---
Relatório gerado e validado em 2026-09-13T23:35:00.
