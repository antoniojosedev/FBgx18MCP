# Movimentação segura de objetos

`genexus_properties action=move` move um objeto para uma Folder ou um Module sem
alterar seu conteúdo. O contrato cobre `Source`, `Rules`, `Events`, `Variables`,
documentação, propriedades e todas as demais partes expostas pelo SDK GeneXus.

## Chamada recomendada

Primeiro a prévia, que devolve o `versionToken` atual; depois a gravação com esse
token como `baseVersion`.

```json
{
  "action": "move",
  "name": "MonitorIntegracaoConteudoArmazenar",
  "type": "Procedure",
  "destination": "Root Module/operacional/Integracao",
  "destKind": "Folder",
  "dryRun": true
}
```

```json
{
  "action": "move",
  "name": "MonitorIntegracaoConteudoArmazenar",
  "type": "Procedure",
  "destination": "Root Module/operacional/Integracao",
  "destKind": "Folder",
  "baseVersion": "<versionToken retornado pela prévia ou por genexus_read>",
  "rollbackOnFailure": true
}
```

## Destino

`destination` aceita o nome da Folder/Module, o caminho (`Root Module/<Module>/<Folder>`,
com o prefixo `Root Module` opcional e `/` ou `\` como separador) ou o GUID do
contêiner. `targetModule`, `folder` e `module` continuam valendo como atalhos que já
carregam o tipo. `destKind=Folder|Module` escolhe entre contêineres de mesmo nome.

A prévia e a gravação resolvem o destino pelo mesmo código, o mesmo resolvedor que
`genexus_create action=object_atomic` usa para `parentPath`/`folder`/`module`:

- `DestinationNotFound`: o caminho não existe; `placement.failedSegment`,
  `placement.resolvedPrefix` e `placement.availableChildren[]` dizem onde ele parou.
- `DestinationAmbiguous`: o nome casa com mais de um contêiner; `placement.candidates[]`
  lista caminho e GUID de cada um. Use o caminho completo, o GUID ou `destKind`.
- `DestinationKindMismatch`: o destino é um Module e foi pedida uma Folder (ou o contrário).
- `DestinationInvalid`: texto vazio, `destKind` desconhecido ou argumentos de posição
  que apontam para lugares diferentes.
- `DestinationReadOnly`: o destino está dentro de um módulo importado (referenciado). O
  `EntityManager.UpdateParent` não passa pela validação de um save normal e aceitaria o
  movimento, enquanto o SDK recusa a criação nesse módulo; os dois são recusados antes de gravar.

## Garantias

- `baseVersion` é obrigatório na gravação. Sem ele a resposta é `BaseVersionRequired`,
  com o token atual em `currentVersion`; a prévia (`dryRun=true`) não exige o token e
  o devolve em `versionToken`. Um token antigo retorna `VersionConflict` antes da
  mutação e é conferido novamente dentro da transação. `expectedVersion` é aceito como
  sinônimo.
- `dryRun=true` executa a mesma resolução de destino e o mesmo preflight da gravação:
  versão, destino contido no próprio objeto (`DestinationInsideMovedObject`), nome já
  usado no módulo de destino (`DestinationNameConflict`), caminho de persistência do
  SDK disponível (`MoveStrategyUnavailable`) e snapshot das partes. Uma recusa lista
  todos os bloqueios em `blockers[]`, com o mesmo código na prévia e na gravação.
- Objetos importados são somente leitura: mover um deles retorna `ObjectReadOnly`. Mover uma
  Folder/Module que contém objetos para outro módulo retorna `ContainerMoveAcrossModules`: o
  banco requalifica os objetos de dentro, mas o Worker em execução mantém as cópias já
  carregadas com o módulo antigo até ser recarregado, e um save posterior a partir delas poderia
  regravar o módulo antigo. Dentro do mesmo módulo, ou com a pasta vazia, o movimento é permitido.
- A prévia não promete o que só o SDK pode confirmar: devolve `canApply`,
  `previewChecked[]`, `previewCannotCheck: ["sdkPersistResult"]`, `persisted=false` e
  `verified=false`. O SDK só revela se grava o novo pai ao gravá-lo; por isso a
  gravação relê o objeto e desfaz o que divergir (abaixo).
- Antes do movimento, todas as partes e propriedades autorais são capturadas e
  resumidas em SHA-256.
- O destino e o snapshot são comparados ainda dentro da transação. Qualquer
  divergência aborta o commit.
- Depois do commit, o objeto é relido pelo SDK com os caches invalidados e resolvido
  pelo GUID, e comparado novamente (posição pela identidade do contêiner, nunca pelo
  nome). Só então a resposta retorna `ObjectMovedAndVerified`, `persisted=true`,
  `verified=true` e `verification.freshRead`.
- Com `rollbackOnFailure=true`, uma divergência restaura o pai e o conteúdo
  anteriores; `rollback.verified` só é `true` quando uma releitura fresca mostra o pai
  original e todas as partes iguais ao snapshot.
- Mover para o módulo de outro nome muda o nome qualificado do objeto; o recibo traz
  `moduleChange {from, to}` nesse caso.
- Um objeto que já está no destino retorna `ObjectAlreadyInDestination` e nada é gravado.
- A operação nunca executa implicitamente Specify, Generate, Build, Rebuild,
  compilação, reorganização, execução ou testes: todo recibo, de sucesso ou de
  falha, traz `implicitOperations: []`.

## Resposta de sucesso

```json
{
  "status": "ok",
  "code": "ObjectMovedAndVerified",
  "result": {
    "from": "Integracao",
    "to": "Integracao",
    "fromPath": "Root Module",
    "toPath": "Root Module/operacional/Integracao",
    "destination": { "requested": "Root Module/operacional/Integracao", "kind": "Folder",
                     "resolved": { "guid": "<guid>", "name": "Integracao", "kind": "Folder",
                                   "path": "Root Module/operacional/Integracao" } },
    "saved": true,
    "persisted": true,
    "verified": true,
    "verification": { "freshRead": true, "positionVerified": true, "partsIdentical": true, "partsCompared": 3 },
    "identity": { "guid": "<guid>", "path": "Root Module/operacional/Integracao/MonitorIntegracaoConteudoArmazenar" },
    "preservedParts": ["Procedure", "Rules", "Variables"],
    "requestedHash": "<sha256>",
    "persistedHash": "<mesmo sha256>",
    "versionToken": "<novo token opaco>",
    "generated": false,
    "implicitOperations": []
  }
}
```

## Falhas

`MoveFailed` informa a causa em `reasonCode` e a fase em `phase`:

| `reasonCode` | Significado |
| --- | --- |
| `SdkPersistRefused` | Nenhum caminho de persistência do SDK aceitou o novo pai; a mensagem lista cada tentativa (`EntityManager.UpdateParent`, `SaveWithParent`, `Save`) e o motivo. |
| `ParentNotPersisted` | O SDK aceitou a chamada, mas a releitura dentro da transação ainda mostra o pai antigo. |
| `ContentChangedInTransaction` | O SDK reescreveu conteúdo ao trocar o pai; `propertyDifferences[]` nomeia a propriedade. |
| `TransactionFailed` | Outra exceção do SDK; a mensagem a reproduz. |

Todas trazem `expectedParent`, `actualParent` e `rollback`. Depois do commit os códigos
são `MoveNotPersisted`, `MoveContentNotPreserved` e `MoveVerificationFailed`. Também
existem `BaseVersionRequired`, `VersionConflict`, `MoveSnapshotFailed`,
`DestinationInsideMovedObject`, `DestinationNameConflict`, `DestinationReadOnly`,
`ObjectReadOnly`, `ContainerMoveAcrossModules`, `MoveStrategyUnavailable` e
`DestinationRootUnsupported` (a Root Module não é um contêiner para onde se possa
mover de volta pelo SDK; use o IDE). Nenhum deles deve ser interpretado como
movimentação confirmada.

## Criação em módulo e pasta

`genexus_create action=object_atomic` aceita `parentPath`, `folder` e `module` (o mesmo
resolvedor acima) e cria o objeto no destino pedido:

1. O destino é resolvido antes de qualquer gravação, também com `dryRun=true`; um destino
   inexistente, ambíguo ou do tipo errado é recusado sem criar nada.
2. O objeto recebe o pai antes do primeiro save. Uma releitura fresca (pelo GUID, com os
   caches do SDK invalidados; nunca o cache de idempotência do Gateway) precisa encontrá-lo
   no contêiner pedido. Se o SDK o deixou em outro lugar, ele é movido pelo mesmo caminho
   do `move`.
3. Se isso também falhar, ou a releitura fresca não estiver disponível, o objeto é
   apagado e uma consulta fresca confirma que ele não existe mais. A resposta é
   `AtomicCreatePlacementFailed`, com `reasonCode` (`PlacementSdkRefused`,
   `PlacementNotPersisted`, `PlacementVerificationFailed`), `placement.attempt` e
   `compensation.rolledBack`.
4. O sucesso devolve `identity` (`guid`, `entityKey`, `path`, `parentPath`, `parentGuid`)
   lida do SDK e `placement` (`resolved`, `path`, `strategy`: `atCreation` ou `move`,
   `verified`, `freshRead`), além de `implicitOperations: []`.

Um objeto que já existe é atualizado onde está; pedir outro lugar retorna
`ObjectExistsElsewhere`. Já existir outro objeto de mesmo nome e tipo no módulo de destino
retorna `DestinationNameConflict`. `validate=true` continua sendo o único caminho que roda
Specify, e só quando pedido.
