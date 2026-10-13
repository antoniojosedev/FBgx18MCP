# Snapshot canônico no salvamento isolado de Events

Uma edição idempotente de Events com `requireObjectSave=true` podia terminar em
`ObjectSaveIncomplete`: Rules aparecia vazia antes do Save e preenchida depois,
embora a leitura independente já retornasse a mesma fonte nos dois momentos.

No SDK U16, a serialização nativa de uma parte de fonte pode consumir um campo
interno ainda não materializado. O snapshot agora lê `ISource.Source` antes de
serializar. Não chama o setter nem salva Rules. A comparação usa fonte ou XML
canônico; a restauração mantém os bytes nativos quando disponíveis.

A prévia captura e compara duas instâncias SDK pelo mesmo capturador do Save.
O Save exclui somente Events da comparação de partes, usando explicitamente a
sobrecarga de lista. Rules, Variables, WebForm, Pattern, propriedades e o
inventário dos demais objetos continuam protegidos antes do commit e na
releitura posterior. Uma mudança real em parte preservada impede o commit.

O recibo expõe formatos, comprimentos e hashes das partes preservadas antes e
depois, sem incluir suas fontes. `partPersisted` descreve a comparação da parte;
`persisted` exige também commit. `transactionCommitted` e `idempotent` tornam o
resultado inequívoco. `isolationTimings` separa o custo do Save dos três
inventários. Um timeout continua exigindo consultar a operação já enviada;
não se deve repetir o Save.

O caminho já auditado de Save isolado reconhece também o conjunto coerente
U16 `18.0.16.189595`, mantendo o conjunto anterior e recusando mistura de seus
componentes. Isso não modifica a compatibilidade geral por major do Worker.
Os aliases de concorrência `baseVersion` e `expectedVersion` usam a mesma
barreira; valores conflitantes são recusados antes do despacho.

A regressão usa uma parte sintética com fonte materializada sob demanda e
serializador que consome o cache. Reverter a ordem reproduz a fonte vazia e
faz o teste falhar. O teste também distingue uma mudança substantiva em Rules.

Na distribuição operacional com esta correção, a validação nativa U16 incluiu
WebPanel sintético com Rules não vazias, prévia sem escrita, Save real, Save
idempotente, token obsoleto e uma alteração inesperada de Rules injetada apenas
no pacote privado de ensaio, com rollback verificado. Um WebPanel com host
WorkWithPlus também concluiu Save idempotente e releitura independente das
partes preservadas. Essa evidência corresponde aos cenários executados e não
substitui novas validações para SDKs ou callbacks ainda não auditados.
