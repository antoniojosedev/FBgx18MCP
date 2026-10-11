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

Em `src/GxMcp.Gateway/ToolHelpCatalog.cs`, no help de `genexus_edit`, seção `Editing pattern parts`, consta: “Measured separately, a standard WorkWith instance save on GX18 *does* regenerate the derived objects”. O trecho não delimita o build medido.

O diagnóstico live, por sua vez, retornou a remediation:

> Editing existing supported properties through genexus_edit part=PatternInstance is possible; structural authoring needs the pattern's typed action, and the pattern's generated objects are not regenerated either way.

Essa frase alcança a edição de propriedades, embora o resultado positivo relatado pelo mantenedor na #353 seja justamente um Save de propriedade existente em GX18.0.10.184260. O nosso reteste em GX18.0.15.188745 não propagou a descrição ao painel. Não generalizamos nenhum dos dois resultados para todo GX18.

Chamada de `genexus_apply_pattern`, somente leitura. Seu schema usa `name` e `pattern`, sem parâmetro `type`:

```json
{"kb":"<KB_TESTE>","name":"<TestTrn>","pattern":"WorkWith","mode":"diagnose","reapply":true,"dryRun":true}
```

A resposta manteve corretamente `reapplyRecommended=false`, `remediationRoute="ide"` e `routeCapabilities.reapply.supported=false`. O trecho a corrigir é a orientação abrangente, não esse bloqueio deliberado de reapply.

Possível correção: distinguir persistência da instância, Save que pode disparar hooks e execução explícita de ApplyPattern/reapply; identificar a proveniência dos resultados medidos e evitar um veredito geral baseado apenas no maior da versão.

## 3. Informação prometida ausente no recibo de patch

No mesmo arquivo e seção do help de `genexus_edit`, consta: “the result carries `derivedObjectRegeneration` with the recorded evidence and its provenance instead of a verdict”. O texto se refere à escrita de instância não WWP e explica que os derivados não são medidos naquele ponto.

O mantenedor já registrou na [resposta à #353](https://github.com/lennix1337/Genexus18MCP/issues/353#issuecomment-5943167316) que o campo só é emitido quando o padrão resolve e pode ficar ausente nas outras escritas. Reconhecemos essa ressalva: a ausência não é uma descoberta nova nem comprovação de falha de regeneração. O residual aqui é alinhar a promessa do help a esse limite conhecido e esclarecer quais rotas fornecem a informação.

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
3. Um patch permitido informa se os derivados foram medidos, não medidos ou se a informação está indisponível, de forma coerente com o help; alternativamente, o help delimita explicitamente as rotas e condições em que essa informação pode estar ausente.
4. A informação de regeneração não transforma persistência verificada em prova de atualização dos derivados e não relaxa o gate de edição estrutural.

## Validação e delimitação

Na auditoria, passaram 65 testes focados do Worker e 17 do Gateway. Há testes de contrato e de forma do fonte nesse conjunto; esses resultados não provam cobertura dos três pontos residuais. A reprodução live mostrou a ausência no recibo e a orientação do diagnóstico. A edição temporária foi restaurada, relida com refresh e confirmada novamente após reabertura da conexão MCP.

A nova issue funcional ligada à #353 trata da ausência de propagação dos derivados nesta instalação. Esta issue trata somente da fidelidade das orientações e do recibo. A #351 fica fora do escopo. A #439 foi atendida no protocolo testado: o refresh observou alteração e restauração pela IDE, sem reabrir a KB.

Buscamos issues abertas e fechadas por `PatternInstance` e `regeneration` em 2026-10-09 e relemos o histórico das #352/#353. Identificamos sobreposição histórica com as #260, #349 e #439, mas não outra issue dedicada a este conjunto residual; isso não é afirmação de busca exaustiva.
