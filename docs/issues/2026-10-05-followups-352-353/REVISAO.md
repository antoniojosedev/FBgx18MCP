# Recibo de revisão dos rascunhos

Escopo autorizado: autorar textos locais e consultar pares, sem publicar. Nas rodadas v1/v2 foram excluídos Opus e modelos GPT. Na v3 o usuário incluiu GPT e manteve somente Opus excluído. Fonte de preferências: resolvedor schema3 do harness Codex, consultado em 2026-10-05; nenhuma preferência foi alterada.

## Rodada v1 — followups-352-353-v1

| Revisor via opencode | Estado | Parecer |
|---|---|---|
| commandcode/deepseek/deepseek-v4.1-flash | responded | Gaps de lastro/evidência e precisão das referências |
| commandcode/stealth/space-bunny-alpha | error | Exit1, stderr vazio; causa não observada; nenhum parecer |
| commandcode/meta/muse-spark-1.3-contributor | responded | Gaps de evidência mínima, comparação de hashes e ressalva sobre cache |

Os dois pareceres foram úteis. Piso efetivo: DeepSeek e Meta; Space Bunny tem criador desconhecido e não conta. O gate autorizou envio público aos três. O dossiê esteve em diretório temporário sem conteúdo integral da KB, credenciais ou ferramentas de escrita/rede disponíveis ao reviewer-ro.

Leitura solicitada: três rascunhos integrais e `fontes.txt`. Os revisores declararam leitura; isso não comprova execução de testes. O orquestrador validou as recomendações contra as fontes e registros da sessão.

## Tratamento na v2

- Adicionado `EVIDENCIAS.md`: projeções live, enum de ações, hashes, componentes temporais de tokens, cronologia IDE e trechos do fonte.
- Explicitada a condição de emissão de `derivedObjectRegeneration`; a identificação de WorkWith na leitura/diagnóstico não prova o manifest usado na escrita.
- Conservada a ressalva de cache mesmo após fechar/reabrir; a invalidação de todas as camadas não foi instrumentada.
- Corrigida a referência de ordenação sem depender de numeração local de linha; justificado seu escopo documental na #352, separado da autoria de filtros da #351.
- Delimitado o caminho ForceSave como trecho do fonte, sem afirmar que a alternativa experimental foi ou não usada.
- Acrescentada a exceção de cache `IsPatternSettingsRead` e a ausência de rastreamento equivalente de `inspect`.
- A possível divergência entre receita WWP e help de WorkWith não foi aceita como contradição de rotas iguais: o texto agora pede delimitar o alcance das orientações.

Estado mecanizado após autoria da v2: `pendingResubmission`, com fechamento bloqueado. V2 enviada aos mesmos três revisores em `followups-352-353-v2`. O resultado dessa rodada será registrado abaixo; não atribuir aprovação da v1 à v2.

## Rodada v2 — followups-352-353-v2

DeepSeek e Muse responderam com pareceres úteis sobre a v2. Space Bunny voltou a encerrar com exit1 e stderr vazio, sem parecer. Os estados finais dos cinco preferidos foram contabilizados: os três acima, mais GPT e Opus como `skippedByHumanDecision`. O piso efetivo permaneceu DeepSeek/Meta.

Os pareceres integrais estão em `pareceres/v2/`; os da rodada anterior, em `pareceres/v1/`. Os prompts e o dossiê enviado também foram preservados. A v2 foi efetivamente ressubmetida; o closeout mecânico retornou `closeoutReady=true`, sem bloqueios de completude/resubmissão. Isso **não significa convergência semântica**: ambos os revisores ainda apontaram gaps.

## Pontos da v2 para triagem humana

- Entradas sanitizadas dos patches, `baseVersion` e recibos dryRun não foram transcritos; o protocolo descrito precisa ser distinguido de um replay portável.
- A captura antiga por nome e a ligação entre identidades sanitizadas não têm demonstração completa no anexo. Muse solicita tokens/hashes por nome e IDs sanitizados estáveis.
- O excerto IDE é menor que o log integral fornecido pelo usuário; explicitar a origem da enumeração ou ampliar o excerto.
- A classificação WWP da receita está afirmada, mas o dossiê não contém seu cabeçalho completo; anexar contexto ou qualificar a inferência.
- Inventariar campos presentes/ausentes nas duas escritas para ajudar a rastrear `derivedObjectRegeneration`.
- Delimitar melhor a proveniência de versão/configuração, do checkbox reportado pelo usuário e da hipótese de hooks comunicada pelo mantenedor.
- Completar os excertos de chave de cache/invalidação, ou preservar explicitamente essa área como não rastreada.
- Ajustar pequenas ambiguidades do enum de actions, do caption de Events e da frase sobre a recomendação de reapply, sem mudar o fato de que o gate continua bloqueado.

Esses itens são apontamentos dos revisores, não conclusões automaticamente aceitas pelo autor. A revisão não convergiu; os textos permanecem rascunhos v2. Não foi produzida uma v3 após essa coleta. Não houve publicação, commit, push, alteração de código do MCP ou nova alteração da KB nesta frente documental.

## Autoria e submissão da v3

Em novo pedido, o usuário autorizou a v3 e outra consulta, incluindo GPT e excluindo apenas Opus. Foram tratados os pontos da v2 sem inventar capturas ausentes:

- Acrescentados templates de requests, rotulados como exemplos para novo ensaio, sem reconstruir `baseVersion` ou recibos dryRun como logs originais.
- Declarada a ausência de reprodução mínima portável e de demonstração independente da ligação entre GUIDs omitidos.
- Explicitadas a captura incompleta por nome e a falta de inventário de campos da escrita por rota.
- Acrescentado o cabeçalho da receita que identifica WWP e o corpo completo da chave de cache.
- Expandida a enumeração sanitizada do log integral fornecido pelo usuário; origem do checkbox e dos metadados de sessão explicitadas.
- Esclarecido que mudou a recomendação de `overrideConflict`, enquanto o gate de reapply continua bloqueado; enum de actions transcrito literalmente.
- Conservado como área não rastreada o conjunto de chamadores de invalidação/cache externo, sem atribuir causa ou exigir um teste que não fizemos.

Rodada: `followups-352-353-v3`. Titulares: DeepSeek, Space Bunny, GPT-6.1 Sol e Muse. Piso pré-despacho: DeepSeek/OpenAI/Meta; Space Bunny continua fora do piso por criador desconhecido. Payload público sanitizado, quatro arquivos de conteúdo e trechos de fontes públicas em `fontes.txt`. A leitura solicitada é igual para todos; não foram atribuídas personas. Estado da v3 antes do resultado: ressubmetida, aguardando pareceres.

## Resultado da v3

| Revisor | Estado técnico | Parecer sobre a v3 |
|---|---|---|
| DeepSeek v4.1 Flash via CommandCode/opencode | responded | SEM GAPS; sugestões opcionais separadas de novos experimentos |
| Space Bunny Alpha via CommandCode/opencode | error | Exit1, stderr vazio; causa não observada, nenhum parecer |
| GPT-6.1 Sol via Codex, esforço medium | responded | SEM GAPS impeditivos; limitações de captura não tornam o relato incoerente |
| Muse Spark1.3 Contributor via CommandCode/opencode | responded | SEM GAPS; relato útil como investigação, sem exigir prova causal |
| Opus | skippedByHumanDecision | Não consultado |

Os três pareceres úteis concordaram sobre a mesma v3. Não foi aplicada mudança de conteúdo depois dessa revisão. Convergência sobre os textos: três criadores conhecidos (DeepSeek/OpenAI/Meta), com Space Bunny sem parecer e sem contagem no piso. Isso não valida os testes de KB por reprodução independente nem elimina as lacunas declaradas no anexo.

Sugestões opcionais preservadas nos pareceres integrais, sem tratá-las como gaps: evitar pressupor uma única pré-condição, explicitar caption do painel, reforçar referências de release/modo quando útil e eventualmente anexar mais capturas de restauração. A sugestão de interpretar frozen como mera ausência de editores não foi adotada: o baseline foi uma versão da KB fechada, como registrado no teste, não apenas uma janela sem concorrência.

Os artefatos da rodada, prompt, dossiê e snapshot exato do conteúdo estão em `pareceres/v3/`. `diversity-v3.json` registra o piso efetivo; `closeout-v3.json` registra `closeoutReady=true`, estados de todos os preferidos e `VNextState=resubmitted`. A aprovação semântica vem dos pareceres acima, não desse booleano.

Status final desta frente: v3 autorada e revisada, pronta para avaliação humana de publicação. Nada publicado, sem commit/push, sem alteração de código do MCP ou nova escrita na KB.

## Publicação posteriormente autorizada

O usuário autorizou a versão final e a publicação. Foram publicados complementos nas #352/#353 e a nova issue #439. `PUBLICACAO.md` registra links, ajustes editoriais de entrega, releitura e comparação integral dos corpos remotos. O status sem publicação acima descreve o encerramento da revisão da v3, anterior a essa nova autorização. Nenhuma aprovação foi atribuída retrospectivamente a uma versão que os pares não leram.
