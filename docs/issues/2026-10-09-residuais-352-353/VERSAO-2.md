# Versão 2 dos rascunhos — 2026-10-09

Estado: autorada com autorização expressa do usuário, **pendente de ressubmissão**, sem publicação. `vNextState=pendingResubmission`. Não se declara aprovação do painel nem convergência.

## Alterações em relação à v1

- Proposta ligada à #352: o item 3 do aceite agora admite expressamente restringir a promessa do help como alternativa à entrega da informação no recibo. Fecha a ambiguidade apontada pelo GPT sem ampliar o pedido para medição automática ou campo incondicional.
- Proposta ligada à #352: reconhece que o mantenedor já havia documentado a possibilidade de ausência de `derivedObjectRegeneration` quando o pattern não resolve. A autoria e o contexto foram conferidos por leitura dos comentários públicos: a ressalva está na resposta do mantenedor à #353, não em comentário dele à #352. O residual é a coerência da promessa do help com o limite conhecido.
- Proposta ligada à #353: a comparação U10/U15 com a mesma fixture fica condicionada à disponibilidade de uma fixture compartilhável. Os aliases do relato não equivalem a disponibilizar a KB nem a comprovar reprodução sintética.

Foram preservados os resultados medidos, os limites de cobertura, a separação entre Save/reapply e entre as duas propostas, a natureza hipotética da pista U14 e a restauração textual já verificada. Nenhum novo teste live foi realizado nesta etapa.

## Proveniência e revisão

Os arquivos `352-orientacoes-e-recibo.md` e `353-save-sem-propagacao.md` desta pasta são agora a v2. A v1 exata permanece em `pareceres/manuscrito-v1.md`. O dossiê `EVIDENCIAS.md` não foi alterado.

`REVISAO.md` descreve exclusivamente a coleta da v1 e conserva seu histórico. O parecer do GPT foi sobre a v1. DeepSeek, Space Bunny e Meta não emitiram parecer por bloqueio do guard estático; Opus foi excluído por decisão humana. Nada disso aprova a v2.

O usuário autorizou a nova autoria com julgamento do autor, mas pediu abrir antes das novas consultas uma sessão em `C:\Dev\Knowledge\GeneXus-XPZ-Skills`, para discutir o bloqueio com o agente daquela pasta. A sessão foi criada com ID `01a120ca-fff3-7151-82bd-c86dfd79813d`, título solicitado “Diagnóstico do bloqueio reviewer-ro na revisão por pares”. Recebeu os comandos, versões, erro exato, comparação da configuração com o contrato, composição do painel, limitações, caminhos dos artefatos e a orientação de discutir antes de alterar qualquer configuração ou disparar modelos.

O usuário escreveu `reviewer-pro`; o perfil medido no adaptador é `reviewer-ro`. A diferença de nomenclatura foi informada no handoff, sem supor outro perfil existente.

Nenhuma consulta adicional aos modelos foi feita para esta v2. Nenhuma configuração global foi alterada pela sessão de origem. A correção do perfil e a próxima consulta continuam pendentes da conversa naquela sessão; não se interpretou esse adiamento como dispensa da revisão.

## Antes de eventual publicação

Resolver as remissões entre as novas issues e disponibilizar o dossiê como anexo ou conteúdo incorporado, conforme já registrado no README. Os novos números/URLs ainda não existem. Não publicar esta nota de revisão como se fosse evidência de teste do MCP.
