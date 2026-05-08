# MCP Friction Report — 2026-05-08

Sessão real: tentei modificar `WebPanel ListaAtiCPAlunoUniGra` e `Procedure RetAluUniGra` na KB `AcademicoHomolog1` usando 100% do MCP. Anotei aqui tudo que travou ou foi confuso pra ajudar a evoluir o produto.

## 1. Criação de SDT estava 100% quebrada

`genexus_create_object type=SDT` retornava sempre `"A validação de Structured Data Type ... falhou"`. Procedure funcionava normalmente.

**Causas (todas no mesmo fluxo, encadeadas):**
- `KBObject.Save()` exige Root.Items com ao menos 1 elemento — não tinha seed.
- `SdtDslParser.Parse` só achava a `SDTStructurePart` por GUID exato; faltava fallback por descName/className/duck-type igual o `Serialize` já tinha.
- `SdtDslParser.SyncSDTNodes` instanciava `SDTItem` por `Activator.CreateInstance(t, new[]{node})`, mas o ctor real só existe como `(SDTItem)` overload. Falhava silencioso (catch vazio).
- A API correta é `SDTLevel.AddItem(string, eDBType[, length[, decimals]])` / `AddLevel(string)`.

Fixei em `4e9334a` (`fix(worker): support SDT creation and Structure DSL writes`).

**Recomendação:** cobrir SDT no `genexus_forge scaffold` também (hoje retorna `Scaffold for type 'SDT' not implemented.`).

## 2. Mensagens de erro genéricas demoram horas pra debugar

`genexus_edit mode=full` na Source retorna apenas `{"error":"Erro","line":1}` quando a validação SDK joga uma exceção genérica. O caminho `mode=patch` retorna a mensagem completa do SDK (ex.: `src0059: Esperando 'EndFor' para fechar 'For Each'`).

**Recomendação:** unificar — sempre tentar capturar `SdkDiagnosticsHelper.GetDiagnostics(obj)` E `part.GetSdkMessages()` antes de retornar `"Erro"`. Hoje o ValidationService captura, mas só quando o pré-flight bate; quando o save SDK lança `Erro` puro sem diagnósticos coletáveis, o usuário fica cego.

## 3. Auto-inject de variável detecta &SDT mas erra o tipo

Ao escrever Source com `&SDTAluInfo.Campo = ...`, o auto-inject criou `&SDTAluInfo : VARCHAR(100)` em vez de bindar como SDT. Resultado: validação subsequente falha com "Erro" genérico (ver problema 2).

**Recomendação:** quando o nome da variável bate com um SDT existente (`Sdt*`/`SDT*`) na KB, ou o uso é `&var.Campo`, tentar resolver como SDT primeiro antes de cair no VARCHAR default.

## 4. `genexus_add_variable` com typeName=SDT cria GX_SDT(4) sem binding visível

Mesmo passando `typeName="SdtAluUniGraInfo"`, o read da Variables DSL mostra `&SDTAluInfo : GX_SDT(4)` — sem indicação do SDT alvo. Internamente o `SetPropertyValue("DataType", targetObj.Key)` foi chamado (log: `Resolved variable SDTAluInfo type to SDT: SdtAluUniGraInfo`), mas o serializador DSL não reflete isso.

**Recomendação:** estender o DSL pra emitir `&SDTAluInfo : SdtAluUniGraInfo` (ou `GX_SDT<SdtAluUniGraInfo>`) quando há binding. Sem isso é impossível confirmar via MCP que o binding foi aplicado.

## 5. Variables part: `mode=patch` não funciona

`genexus_edit part=Variables mode=patch` retorna `Patch read failed: Part does not expose text source`. Só `mode=full` funciona, o que força reescrever o bloco inteiro a cada mudança.

**Recomendação:** ou expor o text source da Variables part (já que serializa via DSL no read), ou avisar mais cedo que patch não é suportado nessa part (e sugerir `genexus_add_variable`).

## 6. Source cache às vezes engole edits

Vi pelo menos uma vez: `genexus_edit mode=patch operation=Append` respondeu `"persistedVerified":true,"patchStatus":"Applied",matchCount:1`, mas o read seguinte mostrou source inalterado. Em outra ocasião, append retornou `NoChange,matchCount:1` quando o conteúdo claramente não existia. Cheirou a `usedSourceCache` desatualizado.

**Recomendação:** invalidar o read cache da part ANTES de aplicar patch (não só depois) — ou pelo menos quando `usedSourceCache=true` E `patchStatus=Applied` mas a verificação posterior falha.

## 7. Deploy do worker travado pelo próprio worker

Subir um fix no worker requer:
1. Build (ok, vai pra `bin/Release`)
2. Matar o worker que está rodando (mata MCP do Claude)
3. Copiar `bin/Release/*` pra `publish/worker/`
4. Próxima chamada MCP respawna do publish

Ciclo doloroso porque o assistente não consegue se "auto-atualizar" sem cortar a conexão. O `dotnet publish` direto falha 100% das vezes com "file is being used by another process".

**Recomendação:** algum dos abaixo:
- Path versionado: gateway lê `publish/worker-*.exe` e usa o mais novo; novo build escreve com timestamp; worker antigo morre quando ocioso.
- Tool `genexus_lifecycle action=reload` que mata o worker e respawna (com warning ao chamador que a próxima call pode demorar).
- Hot-reload via plugin/AssemblyLoadContext (mais complexo).

## 8. Não há tool de `delete_object`

Criei `PrcTesteCriacao` durante debug (e fica órfão na KB) — não tem como apagar via MCP. Tive que pedir pro usuário fazer manualmente no IDE.

**Recomendação:** `genexus_delete_object name=X type=Y` simples, com confirmação obrigatória (e talvez exigir a flag explícita pra evitar acidentes do LLM).

## 9. Pequenas coisas

- `genexus_query` exige `genexus_lifecycle action=index` antes da primeira chamada — ok, mas a mensagem poderia auto-disparar o index (com aviso) em vez de exigir o usuário fazer.
- `genexus_inspect` em uma Table (ex.: `T0001`) retorna `availableParts: ["TableIndexes","TableStructure"]` mas `read part=TableStructure` retorna só `<Properties />` vazio — sem campos. Útil seria expor as colunas/atributos da tabela em texto, similar ao que já é feito pra Trn/SDT no `read part=Structure`.
- O log do worker (`worker_debug.log`) é ouro pra debug, mas não há tool MCP pra ler trecho dele do lado do cliente. Qualquer falha "Erro" exige `Bash + grep` no arquivo. Uma `genexus_logs action=tail lines=50 filterCorrelation=...` resolveria.

## Ranking subjetivo de impacto

1. **Mensagens de erro genéricas (#2)** — maior bloqueio em horas-de-debug perdidas.
2. **SDT criação quebrada (#1)** — agora resolvido.
3. **Auto-inject errado pra SDT (#3+#4)** — quase fez eu desistir do SDT.
4. **Deploy travado (#7)** — atrito severo pra evoluir o próprio MCP.
5. **delete_object ausente (#8)** — incômodo, deixa lixo na KB.
