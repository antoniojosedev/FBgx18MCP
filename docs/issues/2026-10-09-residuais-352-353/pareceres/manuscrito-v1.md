Revise criticamente os dois rascunhos de issues e o dossiê sanitizado abaixo. São propostas para um mantenedor voluntário; não publique, não edite arquivos, não execute MCP/KB/build ou testes mutáveis. Só leitura no material recebido e, se necessário, no fonte público do MCP em C:\Dev\Fork\FBgx18MCP e nas URLs citadas. Não explore outros diretórios locais. Confira as fontes por conta própria dentro desse escopo; se não conseguir, declare a cobertura real. O dossiê é registro consolidado do autor, não log bruto verificável nem fonte de verdade para inventar evidências ausentes.

Avalie precisão factual, separação dos escopos #352/#353, utilidade para reprodução/investigação, linguagem respeitosa, sanitização, critérios de aceite e inferências indevidas sobre U14, cache, hooks ou regeneração. Não exigir que uma fixture privada vire sintética por declaração. Não confundir uma projeção de recibo com o transporte integral. Não pedir novas funcionalidades fora do escopo só para concluir a revisão.

Responda em português com veredito APROVAR SEM GAPS, REVISAR ou REJEITAR; para cada gap, indique arquivo/seção, evidência, impacto e correção mínima. Separe sugestões editoriais opcionais de gaps. Liste arquivos/fontes realmente consultados. Avalie esta versão exata, não uma versão imaginada já corrigida.

--- ARQUIVO: 352-orientacoes-e-recibo.md ---

# [Docs/DX][Follow-up #352] Pontos residuais: exemplo de ordenação, orientação de regeneração e recibo de PatternInstance

Obrigado pelas correções da #352. O preflight estrutural ficou coerente entre patch e full, os exemplos principais de edição estrutural bruta foram corrigidos e o diagnóstico deixou de recomendar o reapply bloqueado. Revalidamos esses avanços no MCP 3.13.0.

Esta issue acompanha o [complemento publicado na #352](https://github.com/lennix1337/Genexus18MCP/issues/352#issuecomment-6003923850), com nova medição em 2026-10-09. Restaram três diferenças entre a orientação publicada e o comportamento disponível. O escopo é documentação, exemplos e informação do recibo; não pedimos implementar filtros nem habilitar reapply.

## Ambiente e efeito para o agente

- MCP carregado nesta sessão: 3.13.0, driver native-sdk, Worker isolado.
- GeneXus instalado: 18.0.15.188745, WorkWith padrão GeneXus 1.2 sobre Transaction existente.
- Fonte local auditada: `0b1b6bb25ce2eafb659f389828ae9371cc3d5293`, após integração do upstream. É a revisão local examinada, não uma identificação do commit público do lançamento nem prova, por si só, de identidade entre fonte e binários.
- Os nomes nos exemplos são aliases sanitizados. Não anexamos fonte de negócio ou credenciais.

O agente pode seguir uma orientação que não aponta para uma chamada disponível, receber afirmações incompatíveis sobre regeneração ou interpretar a gravação verificada da instância como confirmação dos derivados.

## 1. Exemplo de ordenação sem chamada identificável

Em `src/GxMcp.Gateway/ToolHelpCatalog.cs`, seção `Pattern examples`, permanece:

> Add a Selection ordering through the typed WorkWithPlus action or an SDK pattern operation.

O exemplo não identifica ferramenta, ação, argumentos ou pré-requisitos. O enum de `genexus_apply_pattern(mode="actions")` exposto nesta sessão é:

```text
list_actions, add_grid_action, add_user_action, update_action, move_action, remove_action
```

Esses nomes tratam de ações; não identificam uma operação de ordenação da Selection. Não concluímos que tal operação inexista no SDK ou em toda rota possível. O problema verificável é que o exemplo não fornece um caminho executável e verificável pelo agente.

Possível correção: apresentar uma chamada realmente suportada, incluindo o pattern ao qual se aplica; se a autoria não estiver exposta pelo MCP, dizer isso e orientar a IDE. Preservar a proteção de `childrenOrderedList` como metadado do SDK.

## 2. Orientações incompatíveis sobre Save e reapply

O help de `genexus_edit` cita um Save positivo de WorkWith padrão como resultado de GX18, sem delimitar nesse trecho o build medido. O diagnóstico live, por sua vez, retornou a remediation:

> Editing existing supported properties through genexus_edit part=PatternInstance is possible; structural authoring needs the pattern's typed action, and the pattern's generated objects are not regenerated either way.

Essa frase alcança a edição de propriedades, embora o resultado positivo relatado pelo mantenedor na #353 seja justamente um Save de propriedade existente em GX18.0.10.184260. O nosso reteste em GX18.0.15.188745 não propagou a descrição ao painel. Não generalizamos nenhum dos dois resultados para todo GX18.

Chamada de diagnóstico, somente leitura:

```json
{"kb":"<KB_TESTE>","name":"<TestTrn>","pattern":"WorkWith","mode":"diagnose","reapply":true,"dryRun":true}
```

A resposta manteve corretamente `reapplyRecommended=false`, `remediationRoute="ide"` e `routeCapabilities.reapply.supported=false`. O trecho a corrigir é a orientação abrangente, não esse bloqueio deliberado de reapply.

Possível correção: distinguir persistência da instância, Save que pode disparar hooks e execução explícita de ApplyPattern/reapply; identificar a proveniência dos resultados medidos e evitar um veredito geral baseado apenas no maior da versão.

## 3. Informação prometida ausente no recibo de patch

O help afirma que o Save de uma instância não WWP retorna `derivedObjectRegeneration` com a evidência registrada, pois a escrita não mede os derivados naquele ponto.

No teste atual, um patch de descrição existente, endereçado pela Transaction pai com `part="PatternInstance"`, persistiu e foi confirmado por `genexus_read(refresh=true)` sobre a instância. A projeção do recibo foi:

```json
{
  "status": "ok",
  "code": "WriteApplied",
  "result": {
    "part": "PatternInstance",
    "sdkSaveCompleted": true,
    "saved": true,
    "verified": true,
    "persistedVerified": true,
    "persisted": true,
    "implicitLifecycleActions": []
  }
}
```

`derivedObjectRegeneration` não apareceu no recibo examinado. O bloco acima é uma projeção, não o JSON integral; a ausência foi conferida também na resposta integral da chamada na sessão. O protocolo e as limitações constam do dossiê local desta proposta e são reproduzidos no relato funcional relacionado à #353.

O fonte de `WriteService.PatternWrite.cs` continua condicionando o campo a `currentPattern != null && !currentPattern.IsWorkWithPlus`. Não instrumentamos `MatchInstancePattern`, o caminho de Save efetivamente selecionado ou a composição das respostas; a condição é uma pista, não uma causa comprovada.

Possível correção: entregar a informação nas rotas documentadas, inclusive patch pela Transaction, ou restringir a promessa do help. Um estado explícito como “não medido nesta chamada” seria suficiente; não pedimos inferir regeneração a partir de `saved` ou `verified`.

## Aceite sugerido

1. Cada exemplo estrutural identifica chamada suportada e escopo, ou declara a indisponibilidade pelo MCP.
2. Help e diagnóstico distinguem Save de reapply e delimitam os ambientes dos resultados registrados.
3. Um patch permitido informa se os derivados foram medidos, não medidos ou se a informação está indisponível, de forma coerente com o help.
4. A informação de regeneração não transforma persistência verificada em prova de atualização dos derivados e não relaxa o gate de edição estrutural.

## Validação e delimitação

Na auditoria, passaram 65 testes focados do Worker e 17 do Gateway. Há testes de contrato e de forma do fonte nesse conjunto; esses resultados não provam cobertura dos três pontos residuais. A reprodução live mostrou a ausência no recibo e a orientação do diagnóstico. A edição temporária foi restaurada, relida com refresh e confirmada novamente após reabertura da conexão MCP.

A nova issue funcional ligada à #353 trata da ausência de propagação dos derivados nesta instalação. Esta issue trata somente da fidelidade das orientações e do recibo. A #351 fica fora do escopo. A #439 foi atendida no protocolo testado: o refresh observou alteração e restauração pela IDE, sem reabrir a KB.

Buscamos issues abertas e fechadas por `PatternInstance` e `regeneration` em 2026-10-09 e relemos o histórico das #352/#353. Identificamos sobreposição histórica com as #260, #349 e #439, mas não outra issue dedicada a este conjunto residual; isso não é afirmação de busca exaustiva.


--- ARQUIVO: 353-save-sem-propagacao.md ---

# [Investigação][Follow-up #353] Save do WorkWith padrão persiste a instância, mas não propaga a descrição ao painel no GX18 U15

Obrigado pela investigação e pelo experimento positivo documentados na #353. Eles identificaram uma rota candidata do engine e demonstraram que um Save de propriedade existente regenerou derivados no GX18.0.10.184260. Não contestamos essa medição.

Este acompanhamento traz uma divergência reproduzida em outra instalação: com MCP 3.13.0 e GX18.0.15.188745, o Save via MCP persistiu a descrição da Selection, mas o painel gerado conservou o caption anterior. A mesma alteração feita pela IDE chegou ao painel. O [complemento anterior](https://github.com/lennix1337/Genexus18MCP/issues/353#issuecomment-6003912222) registrava esse comportamento na 3.11.1; o reteste de 2026-10-09 usou a leitura fresca entregue para a #439.

## Ambiente e fixture existente

- MCP medido no harness: 3.13.0; native-sdk; Worker isolado.
- GeneXus: `18.0.15.188745+5b2216d446f916f7c0deae363fefd29ea087b8c3`.
- WorkWith padrão: GeneXus 1.2, sobre uma Transaction existente em KB dedicada a testes. Não é WorkWithPlus.
- O checkbox `Apply this pattern on save` já estava marcado na IDE; não o habilitamos como parte do teste.
- Revisão local examinada: `0b1b6bb25ce2eafb659f389828ae9371cc3d5293`, após integração do upstream. Não a apresentamos como commit público do lançamento ou fingerprint dos binários.
- Aliases didáticos: `T` = Transaction, `I` = sua instância WorkWith, `P` = painel WW gerado. As leituras por nome e GUID resolveram a mesma identidade de cada objeto. A identidade real não é publicada.

A KB é legada, hoje sem empresas usando o sistema, e foi disponibilizada pelo usuário para testes reversíveis. Não é uma fixture sintética criada por este relato; não fornecemos XPZ nem afirmamos que uma KB nova reproduza o comportamento. Existia um baseline frozen do ensaio anterior, mas o controle desta rodada foi a restauração textual explícita, não rollback de versão.

## Controle positivo pela IDE, na mesma sessão

1. Ler I.PatternInstance e P.Events por nome e GUID.
2. Na IDE, mudar a descrição da Selection de um valor original para um valor discriminável e salvar.
3. Sem fechar a conexão MCP, ler I e P por nome e GUID com `limit=0, refresh=true`.
4. Confirmar a descrição temporária na instância e em `Form.Caption` do painel.
5. Restaurar a descrição original pela IDE, salvar e repetir as leituras frescas. Os hashes de conteúdo voltaram aos originais; os tokens temporais avançaram, como esperado após novos Saves.

Nesta rodada, as leituras comuns também observaram os novos valores; não reproduzimos o atraso antigo. O controle comprova propagação pela IDE e observação pelo refresh, não isolamento de todos os caches ou de uma corrida concorrente.

## Reteste pelo MCP após a restauração

1. Capturar o texto completo e o token de I.PatternInstance e das partes examinadas de sete derivados, todos com `refresh=true`.
2. Pela Transaction T, fazer dryRun de um patch que altera apenas a descrição existente da Selection. O diff apontou uma única propriedade, com `structuralPreflight="PatternXmlEditPlan"`, `metadataPreserved=true`, `saved=false` e `persisted=false`.
3. Persistir o mesmo patch, com o mesmo `baseVersion`, `expectedCount=1`, `rollbackOnFailure=true` e `verifyMode="exact"`.
4. Reler I e os sete derivados com `refresh=true`, sem solicitar build, reapply ou geração explícita.
5. Restaurar a descrição pela mesma rota, precedida por dryRun e protegida pelo token atualizado. Reler e, adicionalmente, fechar/reabrir a conexão MCP e conferir a restauração persistida.

Forma sanitizada da chamada; placeholders devem ser substituídos e `context` deve vir de uma leitura real:

```json
{
  "kb": "<KB_TESTE>",
  "name": "<T>",
  "type": "Transaction",
  "part": "PatternInstance",
  "mode": "patch",
  "operation": "Replace",
  "context": "    <selection description=\"Original\" page=\"&lt;default&gt;\">",
  "content": "    <selection description=\"ProbeMCP\" page=\"&lt;default&gt;\">",
  "expectedCount": 1,
  "baseVersion": "<TOKEN_DA_LEITURA_ATUAL>",
  "rollbackOnFailure": true,
  "verifyMode": "exact",
  "dryRun": true
}
```

Após validar a prévia, a chamada de persistência mudou somente `dryRun` para `false`. A releitura da instância foi feita também diretamente em I, por tipo WorkWith e parte PatternInstance.

## Resultado medido

| Observação | Save pela IDE | Save pelo MCP |
|---|---|---|
| Descrição persistida em I | Valor temporário visível pelo refresh | Valor temporário visível pelo refresh |
| Caption de P.Events | Recebeu o valor temporário | Conservou o valor original |
| Texto/token das sete partes derivadas | Não fizemos inventário completo nessa etapa | Todos iguais ao baseline imediatamente anterior |
| Restauração | Valor original confirmado pelo refresh | Texto original confirmado pelo refresh e após reabertura |

O recibo do patch retornou `WriteApplied`, `sdkSaveCompleted=true`, `saved=true`, `verified=true`, `persistedVerified=true`, `persisted=true` e `implicitLifecycleActions=[]`. Isso confirma a gravação da instância; a evidência de falta de propagação é a comparação independente do caption e das partes derivadas.

Os sete derivados examinados, usando aliases, foram: `WW<T>.Events`, `View<T>.Events`, `<T>General.Events`, `<T>DetalheWC.Events`, `ExportWW<T>.Source`, `Export<T>DetalheWC.Source` e `General.UI.ListPrograms.Source`. Não examinamos todas as partes, WebForm, propriedades ou todos os objetos possíveis da família. O caption de P era o discriminante, pois havia seguido a alteração pela IDE.

Após restaurar e reabrir a conexão, I voltou exatamente ao texto inicial e as sete partes continuaram iguais ao baseline. Não usamos igualdade de token da instância como critério de restauração: Saves legítimos avançaram sua componente temporal.

## O que ainda não está isolado

- O reteste 3.13.0 exercitou patch pela Transaction pai; não repetiu a escrita direta em I nem mode=full. As duas rotas de patch foram testadas na 3.11.1, como documentado no complemento anterior.
- Não instrumentamos os hooks, a resolução de manifest, `TryPatternDirectSaveExperiment` nem o ramo de Save efetivamente executado. Não atribuímos a falha a uma camada específica.
- Não testamos espera prolongada por geração assíncrona, execução web, build ou XLS. A observação é imediata e de fonte; não se afirma impossibilidade geral de regeneração headless.
- O recibo não trouxe `derivedObjectRegeneration`. Esse ponto de contrato é tratado na nova issue documental ligada à #352, separado deste comportamento funcional.

## Pista de compatibilidade U14, sem concluir causa

A [documentação oficial do Platform SDK](https://docs.genexus.com/en/wiki?27521,GeneXus+Platform+SDK+Download) registra uma mudança a partir do GX18 U14: o instalador separado foi descontinuado e as referências para extensões passaram a ser distribuídas por NuGet, com SDKs de projeto MSBuild.

O Worker deste MCP referencia DLLs `Artech.*` e Patterns da instalação GeneXus, como mostra `src/GxMcp.Worker/GxMcp.Worker.csproj`. É a mesma plataforma de APIs consumida por extensões, com outra cadeia de referências e execução fora da IDE. A mudança oficial de distribuição não prova alteração de contrato de Save, quebra de compatibilidade ou necessidade de migrar o MCP para NuGet. Como as medições U10 e U15 atravessam esse marco, deixamos a informação como pista para comparar APIs e inicialização do pattern.

## Ajuda solicitada e opções de investigação

Seria possível investigar por que a mesma mudança de Selection persiste pelos dois caminhos, mas só o Save pela IDE atualiza o caption neste ambiente?

Alguns cortes que podem ajudar, sem pressupor a solução:

1. Comparar U10 e U15 com a mesma fixture sanitizada, incluindo configuração do pattern e estado de apply-on-save.
2. Instrumentar a rota pela Transaction e a rota direta em I para registrar resolução do pattern, ramo de Save e hooks acionados.
3. Comparar a inicialização dos serviços de pattern no Worker com a IDE; se houver geração diferida, identificar como aguardá-la e medir seu término.
4. Avaliar o candidato `PatternEngine.GenerateInstanceObjects` identificado na #353 somente se necessário, respeitando os limites de acesso e validando os efeitos. Não pedimos habilitar os overloads de reapply já medidos como ineficazes.

Aceite sugerido: na fixture reproduzível, depois de um Save de descrição permitido, o painel derivado reflete a alteração por uma rota suportada e verificável. Se a combinação continuar sem suporte, documentar o limite e a necessidade de aplicar pela IDE, distinguindo claramente instância salva de derivados atualizados.

Na mesma auditoria passaram 82 testes focados (65 Worker, 17 Gateway), incluindo guardas de contrato/fonte que não exercitam esta propagação real. A #350 passou no reteste de preflight; a #439 passou no protocolo bidirecional de refresh. A busca por `PatternInstance` e `regeneration` em issues abertas/fechadas, junto ao histórico da #353, não localizou outro acompanhamento específico desta divergência U15; a busca não é exaustiva.


--- ARQUIVO: EVIDENCIAS.md ---

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
