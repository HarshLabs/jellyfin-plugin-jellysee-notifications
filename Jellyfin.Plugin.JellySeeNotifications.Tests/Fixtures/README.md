These journal and snapshot fixtures were written by the unmodified plugin at
commit a049d94fd6d7c424ada70ae2bacbd03519afaba2, using its Journal writer.
The observed variants have a pending removal with ReaddObservedAt=1002; owed variants also
carry the original add obligation. They test v5 import, restart, and unchanged
predecessor files without regenerating old data with the current serializer.
The bulk fixture preserves an exclusion-enabled v5 bulk with a quiet pending re-add.
