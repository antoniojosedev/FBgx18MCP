# [Investigação] genexus_read devolve conteúdo anterior após Save pela IDE

## Problema observado

Na 3.11.1, alternar entre edição pela IDE e leitura pelo MCP pode devolver fonte e token de versão anteriores ao Save, enquanto `genexus_inspect` já mostra a atualização do objeto. Isso pode induzir o agente a concluir que a IDE não regenerou o pattern ou a preparar uma edição a partir de uma base antiga.

Relato relacionado à #376, mas com uma diferença: nossas chamadas eram **incondicionais**, sem `ifUnchangedSince`. A correção publicada na 3.11.0 para leituras condicionais não é declarada ineficaz por este caso. Antes de abrir uma issue separada, sugerimos conferir se há um contrato de consistência/refresh já documentado que cubra esta situação.

## Ambiente e reprodução observada

MCP3.11.1, SDK18.0.15.188745, native-sdk, KB existente aberta também na IDE. Os nomes abaixo são aliases sanitizados; o teste não criou os objetos do exemplo.

1. Pelo MCP, ler `WorkWithWeb<TestTrn>.PatternInstance` e `WW<TestTrn>.Events` com `limit=0`, conservando texto e versão.
2. Na IDE, alterar a descrição de Selection e salvar com **Apply this pattern on save** marcado. O log confirma geração bem-sucedida.
3. Repetir leituras incondicionais pelo nome: observamos conteúdo anterior, apesar de `inspect` apresentar o timestamp atualizado. Uma leitura por GUID retornou o caption novo.
4. Na IDE, restaurar a descrição e salvar. Agora a leitura por GUID também continuou retornando o caption e token da etapa anterior, enquanto `inspect` apresentava a atualização posterior.
5. Fechar e reabrir a KB pelo MCP. A leitura passou a retornar o conteúdo restaurado.

As duas etapas mostram que usar GUID não é garantia de atualização. Não medimos a duração máxima do atraso, não esperamos deliberadamente todos os TTLs e não isolamos Gateway, cache de resposta do Worker e cache de objetos SDK. O fechamento/reabertura resolveu o caso observado; não o propomos como requisito normal de uso concorrente.

## Pistas do fonte, sem atribuir causa

Para a etapa por nome, não preservamos no anexo uma captura completa de corpo/token por chamada; essa parte é observação registrada na sessão, não prova transcrita para auditoria independente. O exemplo por GUID tem token e cronologia, mas a identidade original também foi omitida. Por isso apresentamos uma investigação de consistência, não uma regressão já isolada em determinada camada.

O anexo `EVIDENCIAS.md` registra timestamps e versões observadas. A reprodução observada é menor que o teste controlado sugerido abaixo; esse teste ainda não foi executado. Não traçamos a rota de `inspect` como equivalente à de `read`.

Na árvore local `46fbe289c5ae2f6b3073ca8f989c88a2e8edebf5`, `ObjectReader` tem cache de resposta com TTL de 60 segundos e exceção para `IsPatternSettingsRead`; `ObjectService` tem outro cache com default de cinco minutos, configurável por `GXMCP_READ_CACHE_TTL_SEC`. O comentário deste último justifica o prazo pela invalidação feita nas escritas. Uma escrita externa na IDE pode não seguir esse caminho. O anexo transcreve a exceção; ainda é necessário verificar a rota efetiva das leituras, notificações, watchers e caches SDK/Gateway nesta configuração. Esses trechos não provam a camada responsável pelo nosso resultado.

## Opções e teste sugerido

A exceção `IsPatternSettingsRead` também inclui GUID/EntityKey sem filtro de tipo. As leituras por GUID do reteste de Save informaram tipo; não traçamos essa classificação em todas as leituras antigas feitas após a IDE. Não assumir que endereçar por GUID sempre bypassa cache. O dossiê contém a chave completa de cache; os chamadores de invalidação e a cobertura de escrita externa continuam fora do rastreamento concluído.

- Validar a revisão persistida do objeto antes de servir um cache como atual e invalidar aliases da mesma identidade quando houver mudança.
- Se consistência eventual for intencional, informar idade/origem do resultado e oferecer refresh de leitura autoritativa, documentado e sem fechar a KB.
- Conferir a cobertura dos sinais de mudança externa. Uma notificação não observada não deve ser assumida como recebida.

Um teste focado: aquecer leitura por nome e GUID, salvar externamente uma alteração discriminável, consultar metadata e repetir leituras incondicionais; comparar corpo e token com leitura fresca. Repetir a restauração, antes e depois dos TTLs, com/sem sinal de atualização. Isso separa atraso esperado de cache SDK persistente e verifica a coerência entre aliases.

Não solicitamos desativar todo cache nem alterar configuração do cliente como correção. O objetivo é esclarecer a garantia de leitura atual e evitar que a automação trate uma resposta antiga como confirmação do estado persistido.
