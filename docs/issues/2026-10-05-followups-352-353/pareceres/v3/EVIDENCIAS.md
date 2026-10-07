# Anexo — evidências selecionadas e limites

Complementa os três textos. Projeções de respostas capturadas em 2026-10-05; não são respostas integrais. Nomes de KB, objetos e GUIDs foram sanitizados. Hashes e componentes temporais dos tokens foram preservados. Não usamos o fonte local como prova de identidade binária com o release publicado.

## Diagnóstico live e schema exposto

`genexus_apply_pattern(kb=<KB>, name=<WorkWith>, pattern="WorkWith", mode="diagnose", reapply=true)`:

```json
{
  "status": "blocked",
  "pattern": {"name": "WorkWith", "publisher": "GeneXus", "version": "1.2"},
  "findings": [
    {
      "reason": "overrideConflict",
      "severity": "info",
      "reapplyRecommended": false,
      "regeneratesDerivedObjects": false,
      "remediationRoute": "ide"
    },
    {
      "reason": "routeUnsupported",
      "remediation": "Read the existing instance with genexus_read part=PatternInstance. Editing existing supported properties through genexus_edit part=PatternInstance is possible; structural authoring needs the pattern's typed action, and the pattern's generated objects are not regenerated either way.",
      "route": "reapply"
    }
  ]
}
```

O enum `action` exposto nesta sessão para `genexus_apply_pattern` contém: `list_actions`, `add_grid_action`, `add_user_action`, `update_action`, `move_action`, `remove_action`. Não contém uma operação de ordenação de Selection. Isso é alcance deste schema, não inventário de toda a API SDK.

## Recibo do patch e comparação após reabrir a KB

Projeção da escrita pelo objeto WorkWith, em `retest353write`:

```json
{
  "code": "WriteApplied",
  "result": {
    "part": "PatternInstance",
    "operation": "replace",
    "expectedCount": 1,
    "matchCount": 1,
    "sdkSaveCompleted": true,
    "saved": true,
    "verified": true,
    "persistedVerified": true,
    "persisted": true
  }
}
```

Na resposta completa capturada não havia `derivedObjectRegeneration`; a projeção acima não tenta demonstrar ausência por omissão. A escrita pela Transaction também devolveu os campos de Save/persistência acima. O protocolo fecha/reabre a KB antes de ler os GUIDs; não é prova de invalidação de toda camada SDK/Gateway.

| Captura | Instância: componente temporal do token | SHA-256 da instância | Events do painel: componente temporal / SHA-256 |
|---|---|---|---|
| Baseline do reteste | `639268277440000000` | `403d8a693e52a8c1e6f8b798124e92ddebd677933df3677dc987c902cea183b9` | `639268277440000000` / `18adef6a645e87337b22d4c1d3918dc458c073e1440c17ce2c1965b05f74f43d` |
| Após patch pelo WorkWith e reabertura | `639268288090000000` | `81c9325d752cd00ab60a3f182edef7d1b0ad02b57f0f258a31b50ce7141ca655` | Igual ao baseline |
| Após patch pela Transaction e reabertura | `639268290570000000` | `e16104dd330b815c4bad3ef3c312a86c289bcee41c5f4f67df1da385be7d5146` | Igual ao baseline |

O trecho `Form.Caption` permaneceu com a descrição original nos dois retestes. A comparação do texto completo de Events também retornou igualdade. O campo `patternName="WorkWith"` e o tipo resolvido `WorkWith` vieram na leitura da instância. O diagnóstico classificou o pattern como GeneXus1.2; não coletamos `currentPattern` dentro do serviço de escrita.

## Controle IDE e leitura antiga

Captura por GUID após o Save IDE: Events do painel tem `Form.Caption` com descrição temporária, token temporal `639268274010000000`, hash `5c7c12db748c87f8a57a8f725ab1d92790f4c9ad0bdca8f573a883f1a47f168c`. O trecho do log reportado pelo usuário, com nomes sanitizados, contém:

```text
Pattern generation (WorkWithWeb<TestTrn>) started
Saving Web Panel 'WW<TestTrn>'... succeeded.
Success: Pattern generation (WorkWithWeb<TestTrn>)
```

Após a restauração pela IDE, `inspect` mostrou atualização `2026-10-05T20:09:04Z`; uma leitura por GUID ainda retornava a etapa anterior, com componente temporal `639268274010000000` correspondente à captura de `2026-10-05T20:03:21Z`. Fechar/reabrir retornou caption original e token temporal `639268277440000000`. Não coletamos uma sequência cronometrada até a expiração dos TTLs. Esta tabela de observações não substitui o teste controlado proposto no relato.

## Fonte pública local examinada

Árvore `46fbe289c5ae2f6b3073ca8f989c88a2e8edebf5`; trechos lidos no disco, sem assumir que todo esse HEAD equivale aos binários 3.11.1 em execução.

- `ToolHelpCatalog.cs`: "Measured separately, a standard WorkWith instance save on GX18 *does* regenerate the derived objects". No exemplo de ordenação: "Add a Selection ordering through the typed WorkWithPlus action or an SDK pattern operation".
- `RecipeCatalog.cs`, `edit_pattern_instance`: "Saving the instance does not regenerate the pattern's generated objects. Apply the pattern in the GeneXus IDE for that." A receita é WWP; seu alcance deve permanecer explícito.
- `WriteService.PatternWrite.cs`: `derivedObjectRegeneration` é criado somente sob `currentPattern != null && !currentPattern.IsWorkWithPlus`, com `measuredAtThisCallSite=false`. A descoberta de manifest usa `MatchInstancePattern`; não foi instrumentada no teste.

O trecho de persistência inclui uma alternativa antes de ForceSave:

```csharp
if (!TryPatternDirectSaveExperiment(resolvedObject))
{
    var prefs = new KBObjectSavePreferences
    {
        ForceSave = true,
        ForceSaveDefaultParts = true,
        SkipValidation = true
    };
    resolvedObject.Save(prefs);
}
```

`ObjectReader`: cache de resposta com TTL60s, ignorado quando `IsPatternSettingsRead` retorna verdadeiro. Essa classificação inclui `PatternSettings` e identidade GUID/EntityKey sem filtro de tipo. Nossas leituras por GUID do reteste informaram tipo explicitamente; ainda não traçamos todas as leituras antigas pela IDE através dessa classificação.

```csharp
bool untypedIdentity = string.IsNullOrWhiteSpace(request.TypeFilter)
    && (!string.IsNullOrWhiteSpace(request.Guid)
        || !string.IsNullOrWhiteSpace(request.EntityKey)
        || Guid.TryParse(request.Target, out _));
return IsSettings(request.TypeFilter) || IsSettings(request.PartName)
    || (request.RequestedParts?.Any(IsSettings) ?? false)
    || IsSettings(request.Target?.Split(':')[0]) || untypedIdentity;
```

`ObjectService`: `ReadCacheTtl` default5min; override `GXMCP_READ_CACHE_TTL_SEC`. A presença desses caches é uma pista; não atribuímos a eles causalidade já demonstrada nem afirmamos que `inspect` percorre o mesmo caminho.

## Proveniência e lacunas de captura

- Versão3.11.1, SDK18.0.15.188745, native-sdk e worker isolado foram medidos pelas ferramentas de diagnóstico da sessão. O JSON integral de identificação não foi incluído neste anexo; são metadados registrados na execução, não deduzidos do HEAD local.
- `updateTransaction="Apply WW Style"` veio da leitura do XML da instância. O checkbox marcado veio do screenshot e da confirmação do usuário. Não medimos seu estado pela API após cada Save.
- A hipótese dos hooks de Save veio do comentário do mantenedor na [#353](https://github.com/lennix1337/Genexus18MCP/issues/353) e do documento `docs/sdk-probe/workwith-regeneration-candidate.md`. É hipótese comunicada, não callback instrumentado.
- Tokens/hashes acima e projeções do recibo foram preservados. As respostas completas de cada dryRun, os `baseVersion` usados e cada leitura antiga por nome não estão anexados. Não reconstruímos essas lacunas como se fossem logs originais.
- A ausência de `derivedObjectRegeneration` nas duas escritas foi verificada na sessão. O inventário completo de `warning`, `patternName`, `resolvedObject` e `resolvedType` por rota não foi preservado neste dossiê; não usamos essa ausência parcial para diagnosticar `MatchInstancePattern`.
- A igualdade dos GUIDs resolvidos foi observada na sessão, mas os GUIDs originais foram omitidos. `T` (Transaction), `I` (instância) e `P` (painel) são rótulos didáticos; não são hashes verificáveis da identidade. O anexo não demonstra independentemente o vínculo entre eles.

## Templates para ensaio, não transcrição das entradas originais

Fixture necessária: Transaction existente com WorkWith padrão aplicado, Selection com descrição existente e painel gerado que usa essa descrição em Events. Não fornecemos fixture XPZ nem afirmamos equivalência estrutural entre a KB testada e uma nova fixture.

Leituras sugeridas que representam a forma usada no reteste (substituir placeholders por identidade real):

```json
{"kb":"<KB>","guid":"<I>","type":"WorkWith","part":"PatternInstance","limit":0}
{"kb":"<KB>","guid":"<P>","type":"WebPanel","part":"Events","limit":0}
```

Patch pela Transaction; obter `baseVersion` da leitura atual, e usar como `context` a linha exata existente. O valor de `context` abaixo é ilustrativo; não é uma captura literal:

```json
{
  "kb":"<KB>",
  "name":"Root Module/Pasta/TestTrn",
  "type":"Transaction",
  "part":"PatternInstance",
  "mode":"patch",
  "operation":"Replace",
  "context":"    <selection description=\"Descrição original\" page=\"&lt;default&gt;\">",
  "content":"    <selection description=\"Descrição temporária\" page=\"&lt;default&gt;\">",
  "expectedCount":1,
  "baseVersion":"<token da leitura atual>",
  "rollbackOnFailure":true,
  "verifyMode":"exact",
  "dryRun":true
}
```

A rota direta usa `name="Root Module/Pasta/WorkWithWebTestTrn"` e `type="WorkWith"`. Persistir somente depois de conferir o dryRun, com a mesma base e `dryRun=false`; depois fechar/reabrir, ler I/P, restaurar pela mesma rota com o token atualizado e conferir. Esses são passos sugeridos para nova medição; não constituem autorização para alterar a KB do leitor.

## Contexto completo da receita WWP e da chave de cache

`RecipeCatalog.cs`, cabeçalho real da receita examinada:

```csharp
["edit_pattern_instance"] = new RecipeMeta(
    "Surgically edit a WWP host's PatternInstance XML without destroying surrounding state.",
    "genexus_recipe { name: 'edit_pattern_instance' }",
    "v1",
```

Seu prerequisite diz: `Host object name is WorkWithPlus<Parent>`. Portanto a classificação WWP está explícita no fonte, não inferida da presença de `childrenOrderedList`.

`ObjectReader.BuildCacheKey` inclui target, GUID, EntityKey, path, parte, janela, formato, tipo e shape:

```csharp
string target = req.Target?.Trim() ?? string.Empty;
string part = req.PartName?.Trim().ToLowerInvariant() ?? "source";
string client = req.ClientFormat?.Trim().ToLowerInvariant() ?? "mcp";
int offset = req.Offset ?? -1;
int limit = req.Limit ?? -1;
int min = req.Minimize ? 1 : 0;
string parts = req.RequestedParts == null ? string.Empty
    : new JArray(req.RequestedParts).ToString(Newtonsoft.Json.Formatting.None);
return $"{target}|{req.Guid}|{req.EntityKey}|{req.Path}|{part}|{offset}|{limit}|{client}|{min}|{req.TypeFilter?.Trim()}|{req.FullObject}|{parts}";
```

O anexo não rastreia todos os chamadores de `Invalidate`/`MarkReadCacheDirty` nem a invalidação externa por IDE. Não há conclusão causal sobre esse ponto.

## Enumeração do log integral fornecido pelo usuário

Transcrição sanitizada: o usuário forneceu essas etapas, sem timestamps por Save. As linhas não demonstram funcionamento da aplicação web.

```text
Pattern generation (WorkWithWeb<TestTrn>) started
Saving Web Panel 'WW<TestTrn>'... succeeded.
Saving Web Panel 'View<TestTrn>'... succeeded.
Saving Web Component '<TestTrn>General'... succeeded.
Saving Web Component '<TestTrn>DetalheWC'... succeeded.
Saving Procedure 'ExportWW<TestTrn>'... succeeded.
Saving Procedure 'Export<TestTrn>DetalheWC'... succeeded.
Saving Transaction '<TestTrn>'... succeeded.
Saving Procedure 'General.UI.ListPrograms'... succeeded.
Success: Pattern generation (WorkWithWeb<TestTrn>)
Reloading Transaction '<TestTrn>'...Done
```
