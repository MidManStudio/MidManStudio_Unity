# com.midmanstudio.patterns: Bytecode

## Modules

### `Opcode.cs`
**What it does:** The instruction set of the virtual machine, 28 instructions. Values are floats on a stack, and 0
is false while any other value is true. Arithmetic (`Add`, `Sub`, `Mul`, `Div`, `Mod`, `Neg`), comparisons that push 1
or 0 (`Eq`, `Ne`, `Lt`, `Le`, `Gt`, `Ge`), logic (`Not`, `And`, `Or`), stack moves (`Pop`, `Dup`, `Swap`,
`PushConst`), locals (`Load`, `Store`), control flow (`Jump`, `JumpIfFalse`, `JumpIfTrue`, `Call`, `Return`, `Halt`) and
`CallHost`. Binary instructions pop the right-hand value first, so `Push 10, Push 4, Sub` gives 6.

### `BytecodeError.cs`
**What it does:** The reasons a program can fail: stack overflow and underflow, call depth, the instruction budget,
an invalid opcode, a truncated instruction, an invalid jump, constant or local, a division by zero and an unknown
host function.

### `BytecodeException.cs`
**What it does:** The exception for those failures. It carries the `Error` and the `Address` where it happened (-1
outside a program) and names the address in its message.

### `HostFunction.cs`
**What it does:** The delegate for a game function that a program can call with `CallHost`. It takes its arguments
with `vm.Pop()`, last argument first, and returns a value, if any, with `vm.Push()`.

### `BytecodeProgram.cs`
**What it does:** An immutable program: an array of ints (instructions with their operands inline), a table of float
constants and the number of local slots. `Disassemble()` gives one line per instruction, and marks bad code
instead of throwing.

**Decisions:**
- The constructor copies the arrays, so changing them afterwards does not change the program.
- Programs can be built with `ProgramBuilder` or loaded as plain arrays from data, which is how designers or mods
  supply behaviour without recompiling.

### `ProgramLabel.cs`
**What it does:** A position in a program that is being built. It is created by the builder and placed with `Mark`.

### `ProgramBuilder.cs`
**What it does:** Assembles a program with chained calls: `Push(float)`, `Emit(opcode)` for instructions without an
operand, `Load`, `Store`, `CallHost`, and `Jump`, `JumpIfFalse`, `JumpIfTrue` and `Call` that name a label. Labels may be
placed before or after the jumps that use them. `Build(localCount)` fills in the addresses.

**How it works:**
- Equal constants share one table entry. Constants are compared by their bits, so 0 and -0 stay separate.
- `Build()` with no argument uses one more than the highest local slot the code touches. A smaller explicit count
  throws `ArgumentException`.
- A label that was never marked, marked twice, or taken from another builder throws `InvalidOperationException`.

### `VirtualMachine.cs`
**What it does:** Runs programs on a stack of floats. `Run(program, arguments)` starts at address 0, puts the
arguments in the first locals and returns the value on top of the stack when the program halts or reaches the end,
or 0 when the stack is empty. `RegisterHost(id, function)` adds a function a program can call. `Push`, `Pop`,
`Peek`, `GetLocal` and `SetLocal` are for host functions and for reading results.

**How it works:**
- The machine has three limits: the stack size (default 256), the call depth (default 64) and the number of
  instructions in one run (default 100000). A program that exceeds one stops with a `BytecodeException`.
- Jump targets, constants, locals and operands are checked while the program runs, not before. A jump to the
  address just past the last instruction ends the program.
- `Return` at the top level ends the program like `Halt`.
- Each run starts with an empty stack and zeroed locals. Locals keep their values after the run, so the host can
  read results. The stack and call stack are allocated once; each run allocates the locals array.

**Decisions:**
- A program can only reach the game through the host functions that were registered. That, together with the three
  limits, is what makes it safe to run programs that came from data or from players.
- Division and modulo by zero are errors and not infinity or NaN, so a bug in a program is visible.
- A host function cannot start another run on the same machine. `Run` throws `InvalidOperationException`.
- An exception from a host function propagates and the machine stays usable.
- Not provided: strings, structured values, a text assembler or compiler, and checking a whole program before
  it runs.
- Not thread-safe. Use one machine per thread.

### `BytecodeTests.cs`
**What it does:** 41 edit-mode NUnit tests: arithmetic and operand order, comparisons and logic, stack moves,
arguments and locals, a loop that sums 1 to 10, conditional jumps, call and return, halting and ending,
host functions (arguments, results, side effects, unknown ids, nested runs, exceptions), every safety limit and
every error kind, address reporting, machine reuse, and the builder (labels, constants, local counts, program
copying, disassembly and argument checks).

**Decisions:**
- Eighteen deliberate breakages were tried (reversed subtraction, division and modulo by zero allowed, the
  budget off by one, the call depth unchecked, a top-level `Return` not handled, the jump-to-end case, an unchecked
  constant index, an inverted `JumpIfFalse`, locals or stack kept between runs, a `Swap` that does nothing, nested
  runs allowed, 0 and -0 merged, a label marked twice, an unmarked label accepted, too few locals accepted and the
  operand left out of the disassembly). All were caught.
  The tests were run outside Unity against a small NUnit stand-in and have not been run in the Unity Test Runner.

## Fixes and Problems

None so far. No bug has been found in a released version.
