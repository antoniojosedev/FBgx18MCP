# Consulta da v2 aos modelos OpenCode — 2026-10-09

Estado: consulta solicitada pelo usuário aos três titulares OpenCode. Sem publicação e sem alteração dos rascunhos após os pareceres. Resultado ainda sem convergência; não somar o parecer do GPT sobre a v1 como aprovação da v2.

## Preparação e limitações

Aplicada a skill `xpz-llm-delegate`, com a metodologia `15-revisao-por-pares.md`. Snapshot atual de preferências, gates e manuscrito exato preservados em `pareceres-opencode-v2/`. Conteúdo público/sanitizado: os dois textos da v2 e o dossiê de evidências. Mesmo prompt para todos, sem lentes distintas. Os revisores foram orientados a emitir parecer próprio e declarar quando as restrições impedissem consultar fonte ou URLs.

Antes da consulta, `Test-OpenCodeReviewerRoInstalledCompatibility.ps1 -AsJson` retornou compatible: OpenCode instalado/testado 1.18.33, definição estática canônica, allow-set read/glob/list, proteção de leitura e external_directory deny. A sessão de origem não alterou configurações.

Os três gates retornaram allow para destinos externos. Piso inicial: DeepSeek e Meta, dois criadores conhecidos; o criador do Space Bunny é oculto e não conta no piso. GPT ficou fora desta rodada porque o usuário pediu os LLMs do OpenCode; Opus continuou excluído.

O dispatcher do painel não produziu recibo ou parecer e ultrapassou o prazo esperado. Após mais de cinco minutos, foi interrompido somente seu processo, com identidade de comando confirmada e sem processos filhos encontrados na árvore naquele momento. Não há prova de transmissão aos modelos nessa tentativa. A causa do atraso não foi identificada. Foram então usados os adaptadores canônicos `Invoke-OpenCode.ps1`, com o mesmo manuscrito, guard padrão e TimeoutSec=240; não se contornaram proteções.

## Parecer obtido do Meta

Muse Spark 1.3 Contributor: **REVISAR**. Cobertura declarada: apenas manuscrito/dossiê recebidos; sem consulta independente ao fonte local ou às URLs. O parecer integral está em `pareceres-opencode-v2/meta.verdict.txt`.

O revisor considerou adequados a separação #352/#353, o tom, a sanitização, a distinção projeção/transporte, as limitações dos testes e a pista U14 sem inferência causal. Indicou quatro pontos:

1. Na #352, indicar arquivo/seção e citações curtas das frases do help sobre Save e `derivedObjectRegeneration`.
2. Na #353, esclarecer que Original/ProbeMCP no exemplo são valores ilustrativos a substituir.
3. Esclarecer a ausência de `type` no diagnose, em contraste com o template de patch.
4. Explicar `limit=0` e as partes lidas no controle IDE.

Opinião do autor para triagem: os pontos 1, 2 e 4 podem melhorar a leitura sem modificar a evidência. O ponto 3 precisa ser conferido no schema específico de `genexus_apply_pattern`, antes de adicionar parâmetro por analogia com outra ferramenta; o parecer não verificou esse contrato. Não classifico esses apontamentos como falhas funcionais comprovadas. Recomendo ajustes pequenos somente depois da triagem, preservando a fidelidade das chamadas relatadas.

Nenhum desses ajustes foi aplicado nesta rodada. A decisão humana e uma eventual nova versão não são substituídas pelo parecer do modelo.

## Space Bunny

Space Bunny Alpha: sem parecer. O adaptador terminou com exit 1 e informou `opencode saiu com codigo 1`, com stderr do processo filho vazio. Não foi observada causa, e isso não comprova recusa do modelo ou defeito do MCP GeneXus.

## Estado da revisão

DeepSeek v4.1 Flash: sem parecer; o adaptador informou `opencode excedeu 240s e foi encerrado`, e terminou com exit 1. A duração total de parede incluiu preparação/validações do adaptador e foi maior que o prazo nominal da chamada. Não foi identificada a causa do timeout.

O resultado é uma **segunda opinião solo do Meta**, com piso pós-coleta insufficientDiversity. O manuscrito foi ressubmetido aos três destinos pedidos; isso não significa que os três emitiram parecer. `vNextState=resubmitted` registra o envio da v2 no escopo explicitamente reduzido pelo usuário; não aprova a versão nem substitui a triagem. Nenhuma v3 foi autorada.

Preservados os erros, textos, preferências e recibos. Nenhuma issue/comentário remoto, commit, push, alteração de código ou operação de KB ocorreu nesta rodada. A v2 permanece a versão vigente dos rascunhos e não está declarada aprovada.
