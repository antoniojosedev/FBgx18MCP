# Dossiê sanitizado — auditoria de 2026-10-09

## Natureza deste registro

Este documento consolida observações das chamadas realizadas nesta conversa. Não é um arquivo bruto do transporte MCP. Os blocos de JSON são projeções ou templates, identificados como tais; nomes de KB/objetos e identidades reais foram omitidos. Não foram preservados aqui os JSONs integrais de todos os retornos. Não tratar a tabela como fixture executável ou prova independente da identidade sanitizada.

## Proveniência

- Fonte local: `C:\Dev\Fork\FBgx18MCP`, HEAD `0b1b6bb25ce2eafb659f389828ae9371cc3d5293`.
- `genexus_whoami`: MCP 3.13.0; GeneXus 18.0.15.188745+5b2216d446f916f7c0deae363fefd29ea087b8c3; native-sdk; Worker isolado; índice Ready.
- PID antes do controle IDE e Save MCP: 33224. Depois da reabertura final: 49140. PIDs são diagnóstico local, não identidades de objeto.
- A revisão Git e a versão medida do servidor são evidências distintas. Não foi feita comparação byte a byte entre o fonte e todo o publish.
- Os callbacks da IDE foram executados pelo usuário. O agente leu os resultados pelo MCP; não controlou remotamente a IDE.

## Sequência e resultados

Horários locais da sessão, fuso -03:00, sem pretender timestamp individual de cada Save IDE:

| Etapa | Observação |
|---|---|
| 09:09 | `refresh=true` funcionou para I.PatternInstance e P.Events; também para leitura de múltiplas partes e objeto completo de P |
| 09:09 | DryRun estrutural por patch e full recusou a mesma inclusão de filterAttribute com PatternStructureChangeUnsupported |
| 09:12–09:14 | Usuário salvou a descrição temporária na IDE e depois restaurou; refresh por nome/GUID observou os dois estados de I e P, sem fechar/reabrir |
| 09:14 | Baseline das sete partes derivadas capturado depois da restauração IDE |
| 09:14–09:15 | Patch de descrição pela Transaction persistido; I mudou, P manteve caption original; sete partes derivadas iguais em texto e token |
| 09:15 | Patch inverso restaurou I; leitura fresca confirmou igualdade exata com o baseline |
| 09:15 | Conexão MCP fechada/reaberta; novo Worker confirmou texto restaurado de I e conteúdo/token inalterados das sete partes |

## Projeções de recibos e leitura

Prévia de propriedade, pela Transaction, projetada:

```json
{
  "status": "ok", "code": "Applied",
  "result": {
    "part": "PatternInstance", "operation": "replace",
    "expectedCount": 1, "matchCount": 1,
    "persisted": false, "saved": false, "verified": false,
    "structuralPreflight": "PatternXmlEditPlan",
    "structuralChanges": [{
      "path": "/instance/[1]/level/[1]/selection/@description",
      "before": "Original", "after": "ProbeMCP"
    }],
    "metadataPreserved": true
  }
}
```

Persistência: `code=WriteApplied`, `sdkSaveCompleted=true`, `saved=true`, `verified=true`, `persistedVerified=true`, `persisted=true`, `implicitLifecycleActions=[]`. A resposta integral foi inspecionada na sessão e não continha `derivedObjectRegeneration`; o dossiê preserva esta observação, não uma reprodução integral do retorno.

Refresh, projeção da indicação de frescor:

```json
{"readFreshness":{"origin":"sdk-refresh","ageMs":0,"sdkCacheRefreshConfirmed":true}}
```

O token de I mudou para o conteúdo temporário e avançou novamente na restauração. Os hashes de conteúdo de I e P retornaram aos originais. “Restaurado” refere-se ao texto; não houve rollback de histórico, timestamps ou versões.

## Matriz do Save MCP

| Parte sanitizada | Texto após patch igual ao baseline | Token após patch igual ao baseline |
|---|---|---|
| WW<T>.Events | Sim; caption original | Sim |
| View<T>.Events | Sim | Sim |
| <T>General.Events | Sim | Sim |
| <T>DetalheWC.Events | Sim | Sim |
| ExportWW<T>.Source | Sim | Sim |
| Export<T>DetalheWC.Source | Sim | Sim |
| General.UI.ListPrograms.Source | Sim | Sim |

A igualdade se repetiu após restauração e reabertura. Isso não equivale a comparar todas as partes de todos os objetos derivados. O painel WW.Events é o controle discriminante porque seu caption mudou no Save IDE.

## Fonte relevante

- `ToolHelpCatalog.cs`: exemplo “Add a Selection ordering…” sem chamada identificada; promessa de `derivedObjectRegeneration`; frase positiva abrangente sobre GX18.
- `PatternApplyService.cs`: remediation que termina com “generated objects are not regenerated either way”; o diagnóstico live retornou essa orientação. Reapply continua explicitamente não suportado.
- `WriteService.PatternWrite.cs`: emissão de `derivedObjectRegeneration` condicionada a `currentPattern != null && !currentPattern.IsWorkWithPlus`; presença de alternativa `TryPatternDirectSaveExperiment` antes de Save com preferências. Nenhum desses ramos foi instrumentado nesta rodada.
- `GxMcp.Worker.csproj`: referências às DLLs de instalação via `GX_PATH`, incluindo `Artech.Packages.Patterns`.
- Referência oficial U14: <https://docs.genexus.com/en/wiki?27521,GeneXus+Platform+SDK+Download>. Confirma a distribuição do SDK por NuGet/MSBuild; não prova mudança em hooks ou Save.

## Testes automatizados efetivamente executados

Worker: 65 aprovados, zero falhas, zero ignorados. Filtro:

```text
RefreshReadTests|PatternInstancePatchPreflightTests|PatternXmlEditPlanTests|DerivedObjectRegenerationReportingTests|PatternReapplyReasonScopeTests
```

Gateway: 17 aprovados, zero falhas, zero ignorados. Filtro:

```text
RefreshReadTests|PatternInstanceHelpContractTests|ApplyPatternSchemaPromiseTests
```

Executados com `dotnet test -c Release --no-build --no-restore`, filtros FullyQualifiedName e logger mínimo; Worker com Platform=x86. As DLLs Release tinham timestamp de 2026-10-09 08:58. A primeira tentativa do Gateway no sandbox foi anulada por timeout de conexão do testhost; a repetição fora do sandbox passou. Não classificamos essa falha de infraestrutura como defeito do produto. Os testes não substituem a comparação live de derivados.

## Limites e recuperação

Sem instrumentação de hooks/manifest; sem teste atual de Save direto em I ou mode=full; sem espera prolongada por eventual geração assíncrona; sem comparação WebForm/metadata; sem build, XLS ou execução web. O baseline frozen anterior existia; a restauração desta rodada usou patch inverso com versão atualizada e read-back, não reversão de versão.

Nenhuma publicação foi feita nesta rodada. Não houve patch no código do MCP. Não alegamos restauração de timestamps ou histórico da KB.
