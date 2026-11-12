# Artisan solver sources (third-party, test-only)

The `.cs` files in this folder other than `ArtisanShim.cs` are copied from
[PunishXIV/Artisan](https://github.com/PunishXIV/Artisan) at commit
`c8ee6e0b70e7ca08dcae73d6546095e3baeed9c5` by `tools/vendor-artisan-solvers.py`. Each file's header
lists the exact edits that script made (removed game/UI code only; the solver logic is untouched).

They exist so Hephaestus's benchmark can play **Artisan's own decision logic** against Hephaestus's
on the same simulated craft. They are compiled into the test assembly only and are never part of
the Hephaestus plugin.

`ArtisanShim.cs` is hand-written for Hephaestus and is not Artisan code.

## Licence of the copied files

BSD 3-Clause License

Copyright (c) 2023, Puni.sh

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
