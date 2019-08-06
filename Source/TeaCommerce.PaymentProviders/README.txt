We maintain an empty PaymentProviders project in order to create a blank DLL into which
we will merge all the individual payment providers at build time. We do this in order to
maintain backwards compatability with previous Tea Commerce releases where the 
payment providers were historicaly combined in a single DLL so we continue to do this
to prevent any breaking changes should anyone be referencing them.

The version number for this project (in the root) then will maintain the version numbering of the 
original payment provider DLL and so will likely be out of sync with the indiviual providers