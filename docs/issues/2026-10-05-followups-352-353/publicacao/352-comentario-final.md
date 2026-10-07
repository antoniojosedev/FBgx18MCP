# Complemento da #352 — orientações residuais e evidência de regeneração

Obrigado pelas correções entregues na 3.10.0. Na 3.11.1, confirmamos a mudança de recomendação em `overrideConflict`: agora indica a IDE e retorna `reapplyRecommended=false`. O gate de reapply continua bloqueado. Os exemplos principais de autoria estrutural pela rota bruta também foram substituídos. Este complemento trata apenas dos pontos residuais abaixo.

## Observações

1. `ToolHelpCatalog.cs`, no exemplo "Add a Selection ordering", orienta uma ação tipada WorkWithPlus ou operação SDK, mas não identifica ferramenta, ação e argumentos disponíveis. Para `mode="actions"`, o enum de `action` exposto é `list_actions`, `add_grid_action`, `add_user_action`, `update_action`, `move_action`, `remove_action`; não nomeia ordenação de Selection. Isso não prova ausência no SDK. É um problema do exemplo já abrangido pela #352; a #351 trata da inclusão de filtros e permanece fora deste pedido.
2. O help relata um experimento positivo de Save de WorkWith padrão no GX18. A receita `edit_pattern_instance` diz "Saving the instance does not regenerate the pattern's generated objects". Ela é WWP, portanto não usamos essa frase isolada como contradição entre rotas equivalentes: pedimos identificar o alcance de cada orientação. No diagnóstico live de WorkWith, a remediation afirma que os derivados não são regenerados "either way". As citações e a projeção da resposta estão em [anexo publicado no complemento da #353](https://github.com/lennix1337/Genexus18MCP/issues/353#issuecomment-6003912222); o complemento da #353 traz o resultado específico desta instalação.
3. O help promete `derivedObjectRegeneration` para instâncias não WWP, mas os patches testados de WorkWith padrão não o devolveram. O fonte condiciona a emissão a `currentPattern != null && !currentPattern.IsWorkWithPlus`. O diagnóstico identificou WorkWith GeneXus 1.2 e a leitura identificou a mesma instância; isso não comprova que `MatchInstancePattern` retornou um manifest na escrita. O pedido é verificar resolução e composição da resposta, não atribuir a perda a uma camada determinada. O recibo e os limites da comparação estão no anexo e na #353.

## Proposta delimitada

- Para ordenação, publicar uma chamada real suportada com pré-requisitos e capability; se não houver, retirar esse exemplo ou declarar a indisponibilidade da autoria pelo MCP.
- Qualificar a observação positiva como medida na GX18.0.10.184260, sem generalizar a todo GX18. Conservar a distinção entre as rotas de Save e reapply.
- Fazer a informação de regeneração chegar à resposta das rotas documentadas, inclusive patch, ou ajustar a promessa do help. Persistência verificada não deve ser apresentada como prova de regeneração.

## Aceite sugerido

Exemplos devem apontar para ferramenta/ação exposta; help e diagnóstico devem ser coerentes sobre o escopo da evidência. Um patch permitido de propriedade deve informar se a regeneração foi medida, não medida ou indisponível, sem inferi-la de `saved=true`.

Não pedimos habilitar reapply nem implementar filtros neste complemento. A #351 permanece fora deste acompanhamento. Evidência: MCP live 3.11.1, SDK18.0.15.188745; leitura local do fonte em `46fbe289c5ae2f6b3073ca8f989c88a2e8edebf5`. O fonte local não é apresentado como o commit público do lançamento.

O [complemento da #353](https://github.com/lennix1337/Genexus18MCP/issues/353#issuecomment-6003912222) contém o protocolo, as projeções e as limitações. Esta observação documental não pede reabrir a implementação de filtros da #351.
