# Revisão dos rascunhos — rodada v1

Estado: dois rascunhos autorados, sem publicação. Coleta encerrada; revisão por pares pendente por bloqueio técnico de três revisores e triagem humana de um apontamento. Não houve versão v2; `vNextState=notProduced`.

## Material e método

Aplicadas a skill `xpz-llm-delegate` e a metodologia `15-revisao-por-pares.md`, lidas em `C:\Dev\Knowledge\GeneXus-XPZ-Skills`. O mesmo prompt, os dois textos integrais e o dossiê sanitizado foram preparados para os quatro destinos autorizados. O GPT recebeu o material; os outros três adaptadores bloquearam antes de entregar parecer, sem comprovação de envio aos modelos. O manuscrito classificou explicitamente o dossiê como relato consolidado, sem logs brutos, e proibiu publicação, edição e operações na KB. Conteúdo classificado como público/sanitizado.

Preferências resolvidas para Codex em `C:\Users\ANTONIOJOSE\AppData\Local\xpz-llm-delegate\preferred-reviewers.codex.json`. Opus excluído por decisão anterior do usuário; GPT incluído. Os gates de envio dos quatro candidatos retornaram allow. A preferência não foi alterada.

## Resultado por destino

| Revisor | Criador | Resultado |
|---|---|---|
| DeepSeek v4.1 Flash, via OpenCode | DeepSeek | Erro no guard estático; sem parecer |
| Space Bunny Alpha, via OpenCode | Oculto/desconhecido | Erro no guard estático; sem parecer; não conta no piso |
| GPT 6.1 Sol, via Codex, esforço medium aplicado | OpenAI | REVISAR: uma ambiguidade no aceite da #352 |
| Claude Opus | Anthropic | Excluído por decisão humana; sem despacho |
| Muse Spark 1.3 Contributor, via OpenCode | Meta | Erro no guard estático; sem parecer |

O piso inicial tinha três criadores conhecidos. Após as tentativas, houve parecer utilizável de somente um criador. Portanto, o resultado é **segunda opinião solo**, não revisão por pares concluída. O recibo mecânico de completude não substitui esse limite nem decide a triagem.

## Apontamento do GPT e recomendação do autor

O §3 da proposta ligada à #352 aceita entregar informação no recibo **ou restringir a promessa do help**. O item 3 do aceite exige informação no recibo sem preservar expressamente a segunda alternativa.

Concordo com a pertinência do apontamento. Recomendo substituir esse item por:

> Um patch permitido informa se os derivados foram medidos, não medidos ou se a informação está indisponível, de forma coerente com o help; alternativamente, o help delimita explicitamente as rotas e condições em que essa informação pode estar ausente.

Esta é uma proposta de correção para triagem, ainda não aplicada. A metodologia consultada reserva ao humano a decisão sobre gaps. Não se alterou silenciosamente a versão já enviada ao revisor.

O GPT não apontou gaps impeditivos na proposta ligada à #353 nem no dossiê, dentro da cobertura declarada. Isso é o parecer de um revisor, não comprovação independente dos testes live.

Sugestões editoriais opcionais do GPT:

1. Reconhecer no §3 da proposta #352 que o mantenedor já documentou publicamente a possibilidade de ausência do campo; o residual é a promessa abrangente do help.
2. Na investigação da #353, condicionar a comparação com fixture compartilhável à disponibilidade dessa fixture.
3. Antes de publicar, resolver as remissões entre as novas issues e tornar o dossiê acessível. O README já registra esse preparo.

O revisor consultou fonte relevante do MCP, histórico público das #352/#353 e documentação oficial do SDK. Não repetiu os testes, não auditou logs brutos nem refez a busca de duplicidade. O parecer integral está preservado em `pareceres/v1/02-openai-openai-gpt-6.1-sol.verdict.txt`.

## Bloqueio técnico das outras três vozes

Mensagem dos adaptadores:

```text
BLOCK: guard reviewer-ro fail-closed (motivo=static): mapa read: chaves/ordem divergentes
```

O diagnóstico somente leitura `Test-OpenCodeReviewerRoInstalledCompatibility.ps1 -AsJson` confirmou OpenCode instalado/testado 1.18.33. A definição efetiva está em `C:\Users\ANTONIOJOSE\.config\opencode\opencode.jsonc`, com `permission.read` escalar `allow`. O contrato atual exige um mapa ordenado: `*=allow`, `*.env=deny`, `*.env.*=deny`, `.env.example=allow`, `*/.env.example=allow`.

O guard rejeitou a definição antes dos pareceres. A comparação não avaliou o allow-set final, pois a validação estática já falhou. Não foi alteração nem recusa do MCP GeneXus. Nenhuma configuração, proteção ou preferência foi modificada; não se contornou o guard.

Para retomar o painel é necessário adequar o perfil por uma alteração de configuração explicitamente autorizada e revalidar os gates. Depois da triagem e eventual alteração dos textos, a nova versão precisa ser enviada ao painel; aprovação da v1 não aprova automaticamente uma v2.

## Artefatos e estado Git

Os manuscritos, snapshot de preferências, gates, recibos, parecer integral e erros técnicos estão preservados nesta subpasta `pareceres/`. Os caminhos absolutos presentes nos recibos descrevem o local original da execução, no workspace de suporte; as cópias aqui preservam esse registro.

Somente documentação e artefatos de revisão foram criados no fork, em `main`. Não houve commit, push, issue, comentário remoto ou alteração de código/KB nesta etapa. O pacote anterior foi preservado.
