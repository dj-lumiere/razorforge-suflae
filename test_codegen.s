	.def	@feat.00;
	.scl	3;
	.type	0;
	.endef
	.globl	@feat.00
@feat.00 = 0
	.file	"razorforge_module"
	.def	add_s64;
	.scl	2;
	.type	32;
	.endef
	.text
	.globl	add_s64                         # -- Begin function add_s64
	.p2align	4
add_s64:                                # @add_s64
.seh_proc add_s64
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movq	%rcx, 48(%rsp)
	movq	%rdx, 40(%rsp)
	callq	S64___add__
	nop
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	sub_s64;
	.scl	2;
	.type	32;
	.endef
	.globl	sub_s64                         # -- Begin function sub_s64
	.p2align	4
sub_s64:                                # @sub_s64
.seh_proc sub_s64
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movq	%rcx, 48(%rsp)
	movq	%rdx, 40(%rsp)
	callq	S64___sub__
	nop
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	mul_s64;
	.scl	2;
	.type	32;
	.endef
	.globl	mul_s64                         # -- Begin function mul_s64
	.p2align	4
mul_s64:                                # @mul_s64
.seh_proc mul_s64
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movq	%rcx, 48(%rsp)
	movq	%rdx, 40(%rsp)
	callq	S64___mul__
	nop
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	div_s64;
	.scl	2;
	.type	32;
	.endef
	.globl	div_s64                         # -- Begin function div_s64
	.p2align	4
div_s64:                                # @div_s64
.seh_proc div_s64
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movq	%rcx, 48(%rsp)
	movq	%rdx, 40(%rsp)
	callq	S64___floordiv__
	nop
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	add_f64;
	.scl	2;
	.type	32;
	.endef
	.globl	add_f64                         # -- Begin function add_f64
	.p2align	4
add_f64:                                # @add_f64
.seh_proc add_f64
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movsd	%xmm0, 48(%rsp)
	movsd	%xmm1, 40(%rsp)
	callq	F64___add__
	nop
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	is_positive;
	.scl	2;
	.type	32;
	.endef
	.globl	is_positive                     # -- Begin function is_positive
	.p2align	4
is_positive:                            # @is_positive
.seh_proc is_positive
# %bb.0:                                # %entry
	subq	$40, %rsp
	.seh_stackalloc 40
	.seh_endprologue
	movq	%rcx, 32(%rsp)
	xorl	%edx, %edx
	callq	S64___gt__
	nop
	.seh_startepilogue
	addq	$40, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	is_equal;
	.scl	2;
	.type	32;
	.endef
	.globl	is_equal                        # -- Begin function is_equal
	.p2align	4
is_equal:                               # @is_equal
.seh_proc is_equal
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movq	%rcx, 48(%rsp)
	movq	%rdx, 40(%rsp)
	callq	S64___eq__
	nop
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	max_s64;
	.scl	2;
	.type	32;
	.endef
	.globl	max_s64                         # -- Begin function max_s64
	.p2align	4
max_s64:                                # @max_s64
.seh_proc max_s64
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movq	%rcx, 48(%rsp)
	movq	%rdx, 40(%rsp)
	callq	S64___gt__
	testb	$1, %al
	je	.LBB7_3
# %bb.1:                                # %if_then0
	movq	48(%rsp), %rax
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
.LBB7_3:                                # %if_else2
	movq	40(%rsp), %rax
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	min_s64;
	.scl	2;
	.type	32;
	.endef
	.globl	min_s64                         # -- Begin function min_s64
	.p2align	4
min_s64:                                # @min_s64
.seh_proc min_s64
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movq	%rcx, 48(%rsp)
	movq	%rdx, 40(%rsp)
	callq	S64___lt__
	testb	$1, %al
	je	.LBB8_3
# %bb.1:                                # %if_then3
	movq	48(%rsp), %rax
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
.LBB8_3:                                # %if_else5
	movq	40(%rsp), %rax
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	abs_s64;
	.scl	2;
	.type	32;
	.endef
	.globl	abs_s64                         # -- Begin function abs_s64
	.p2align	4
abs_s64:                                # @abs_s64
.seh_proc abs_s64
# %bb.0:                                # %entry
	subq	$40, %rsp
	.seh_stackalloc 40
	.seh_endprologue
	movq	%rcx, 32(%rsp)
	xorl	%edx, %edx
	callq	S64___lt__
	testb	$1, %al
	je	.LBB9_2
# %bb.1:                                # %if_then6
	movq	32(%rsp), %rdx
	xorl	%ecx, %ecx
	callq	S64___sub__
	nop
	.seh_startepilogue
	addq	$40, %rsp
	.seh_endepilogue
	retq
.LBB9_2:                                # %if_end7
	movq	32(%rsp), %rax
	.seh_startepilogue
	addq	$40, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	clamp;
	.scl	2;
	.type	32;
	.endef
	.globl	clamp                           # -- Begin function clamp
	.p2align	4
clamp:                                  # @clamp
.seh_proc clamp
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movq	%rcx, 40(%rsp)
	movq	%rdx, 48(%rsp)
	movq	%r8, 32(%rsp)
	callq	S64___lt__
	testb	$1, %al
	je	.LBB10_3
# %bb.1:                                # %if_then8
	movq	48(%rsp), %rax
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
.LBB10_3:                               # %if_end9
	movq	40(%rsp), %rcx
	movq	32(%rsp), %rdx
	callq	S64___gt__
	testb	$1, %al
	je	.LBB10_5
# %bb.4:                                # %if_then10
	movq	32(%rsp), %rax
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
.LBB10_5:                               # %if_end11
	movq	40(%rsp), %rax
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	factorial;
	.scl	2;
	.type	32;
	.endef
	.globl	factorial                       # -- Begin function factorial
	.p2align	4
factorial:                              # @factorial
.seh_proc factorial
# %bb.0:                                # %entry
	pushq	%rsi
	.seh_pushreg %rsi
	subq	$48, %rsp
	.seh_stackalloc 48
	.seh_endprologue
	movq	%rcx, 40(%rsp)
	movl	$1, %edx
	callq	S64___le__
	testb	$1, %al
	je	.LBB11_3
# %bb.1:                                # %if_then12
	movl	$1, %eax
	jmp	.LBB11_2
.LBB11_3:                               # %if_end13
	movq	40(%rsp), %rsi
	movl	$1, %edx
	movq	%rsi, %rcx
	callq	S64___sub__
	movq	%rax, %rcx
	callq	factorial
	movq	%rsi, %rcx
	movq	%rax, %rdx
	callq	S64___mul__
.LBB11_2:                               # %if_then12
	nop
	.seh_startepilogue
	addq	$48, %rsp
	popq	%rsi
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	fibonacci;
	.scl	2;
	.type	32;
	.endef
	.globl	fibonacci                       # -- Begin function fibonacci
	.p2align	4
fibonacci:                              # @fibonacci
.seh_proc fibonacci
# %bb.0:                                # %entry
	pushq	%rsi
	.seh_pushreg %rsi
	subq	$48, %rsp
	.seh_stackalloc 48
	.seh_endprologue
	movq	%rcx, 40(%rsp)
	movl	$1, %edx
	callq	S64___le__
	testb	$1, %al
	je	.LBB12_3
# %bb.1:                                # %if_then14
	movq	40(%rsp), %rax
	jmp	.LBB12_2
.LBB12_3:                               # %if_end15
	movq	40(%rsp), %rcx
	movl	$1, %edx
	callq	S64___sub__
	movq	%rax, %rcx
	callq	fibonacci
	movq	%rax, %rsi
	movq	40(%rsp), %rcx
	movl	$2, %edx
	callq	S64___sub__
	movq	%rax, %rcx
	callq	fibonacci
	movq	%rsi, %rcx
	movq	%rax, %rdx
	callq	S64___add__
.LBB12_2:                               # %if_then14
	nop
	.seh_startepilogue
	addq	$48, %rsp
	popq	%rsi
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	sum_to_n;
	.scl	2;
	.type	32;
	.endef
	.globl	sum_to_n                        # -- Begin function sum_to_n
	.p2align	4
sum_to_n:                               # @sum_to_n
.seh_proc sum_to_n
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movq	%rcx, 48(%rsp)
	movq	$0, 40(%rsp)
	movq	$1, 32(%rsp)
	.p2align	4
.LBB13_1:                               # %while_cond16
                                        # =>This Inner Loop Header: Depth=1
	movq	32(%rsp), %rcx
	movq	48(%rsp), %rdx
	callq	S64___le__
	testb	$1, %al
	jne	.LBB13_1
# %bb.2:                                # %while_end18
	movq	40(%rsp), %rax
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	factorial_iter;
	.scl	2;
	.type	32;
	.endef
	.globl	factorial_iter                  # -- Begin function factorial_iter
	.p2align	4
factorial_iter:                         # @factorial_iter
.seh_proc factorial_iter
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movq	%rcx, 48(%rsp)
	movq	$1, 40(%rsp)
	movq	$2, 32(%rsp)
	.p2align	4
.LBB14_1:                               # %while_cond19
                                        # =>This Inner Loop Header: Depth=1
	movq	32(%rsp), %rcx
	movq	48(%rsp), %rdx
	callq	S64___le__
	testb	$1, %al
	jne	.LBB14_1
# %bb.2:                                # %while_end21
	movq	40(%rsp), %rax
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	count_digits;
	.scl	2;
	.type	32;
	.endef
	.globl	count_digits                    # -- Begin function count_digits
	.p2align	4
count_digits:                           # @count_digits
.seh_proc count_digits
# %bb.0:                                # %entry
	subq	$56, %rsp
	.seh_stackalloc 56
	.seh_endprologue
	movq	%rcx, 48(%rsp)
	callq	abs_s64
	movq	%rax, 40(%rsp)
	movq	$0, 32(%rsp)
	movq	%rax, %rcx
	xorl	%edx, %edx
	callq	S64___eq__
	testb	$1, %al
	je	.LBB15_3
# %bb.1:                                # %if_then22
	movl	$1, %eax
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.p2align	4
.LBB15_3:                               # %while_cond24
                                        # =>This Inner Loop Header: Depth=1
	movq	40(%rsp), %rcx
	xorl	%edx, %edx
	callq	S64___gt__
	testb	$1, %al
	jne	.LBB15_3
# %bb.4:                                # %while_end26
	movq	32(%rsp), %rax
	.seh_startepilogue
	addq	$56, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	sum_range;
	.scl	2;
	.type	32;
	.endef
	.globl	sum_range                       # -- Begin function sum_range
	.p2align	4
sum_range:                              # @sum_range
.seh_proc sum_range
# %bb.0:                                # %entry
	subq	$24, %rsp
	.seh_stackalloc 24
	.seh_endprologue
	movq	%rcx, 16(%rsp)
	movq	%rdx, 8(%rsp)
	movq	$0, (%rsp)
	.p2align	4
.LBB16_1:                               # %for_cond27
                                        # =>This Inner Loop Header: Depth=1
	jmp	.LBB16_1
	.seh_endproc
                                        # -- End function
	.def	power;
	.scl	2;
	.type	32;
	.endef
	.globl	power                           # -- Begin function power
	.p2align	4
power:                                  # @power
.seh_proc power
# %bb.0:                                # %entry
	subq	$24, %rsp
	.seh_stackalloc 24
	.seh_endprologue
	movq	%rcx, 16(%rsp)
	movq	%rdx, 8(%rsp)
	movq	$1, (%rsp)
	.p2align	4
.LBB17_1:                               # %for_cond31
                                        # =>This Inner Loop Header: Depth=1
	jmp	.LBB17_1
	.seh_endproc
                                        # -- End function
	.def	gcd;
	.scl	2;
	.type	32;
	.endef
	.globl	gcd                             # -- Begin function gcd
	.p2align	4
gcd:                                    # @gcd
.seh_proc gcd
# %bb.0:                                # %entry
	pushq	%rbp
	.seh_pushreg %rbp
	subq	$32, %rsp
	.seh_stackalloc 32
	leaq	32(%rsp), %rbp
	.seh_setframe %rbp, 32
	.seh_endprologue
	movq	%rcx, -32(%rbp)
	movq	%rdx, -24(%rbp)
	subq	$32, %rsp
	callq	abs_s64
	addq	$32, %rsp
	movq	%rax, -16(%rbp)
	movq	-24(%rbp), %rcx
	subq	$32, %rsp
	callq	abs_s64
	addq	$32, %rsp
	movq	%rax, -8(%rbp)
	.p2align	4
.LBB18_1:                               # %while_cond35
                                        # =>This Inner Loop Header: Depth=1
	movq	-8(%rbp), %rcx
	subq	$32, %rsp
	xorl	%edx, %edx
	callq	S64___gt__
	addq	$32, %rsp
	testb	$1, %al
	je	.LBB18_3
# %bb.2:                                # %while_body36
                                        #   in Loop: Header=BB18_1 Depth=1
	movl	$16, %eax
	callq	__chkstk
	subq	%rax, %rsp
	movq	%rsp, %rax
	movq	-8(%rbp), %rcx
	movq	%rcx, (%rax)
	jmp	.LBB18_1
.LBB18_3:                               # %while_end37
	movq	-16(%rbp), %rax
	.seh_startepilogue
	movq	%rbp, %rsp
	popq	%rbp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.def	is_prime;
	.scl	2;
	.type	32;
	.endef
	.globl	is_prime                        # -- Begin function is_prime
	.p2align	4
is_prime:                               # @is_prime
.seh_proc is_prime
# %bb.0:                                # %entry
	pushq	%rbp
	.seh_pushreg %rbp
	pushq	%rsi
	.seh_pushreg %rsi
	pushq	%rdi
	.seh_pushreg %rdi
	subq	$16, %rsp
	.seh_stackalloc 16
	leaq	16(%rsp), %rbp
	.seh_setframe %rbp, 16
	.seh_endprologue
	movq	%rcx, -8(%rbp)
	subq	$32, %rsp
	movl	$2, %edx
	callq	S64___lt__
	addq	$32, %rsp
	testb	$1, %al
	je	.LBB19_3
.LBB19_1:                               # %if_then38
	xorl	%eax, %eax
	jmp	.LBB19_2
.LBB19_3:                               # %if_end39
	movq	-8(%rbp), %rcx
	subq	$32, %rsp
	movl	$2, %edx
	callq	S64___eq__
	addq	$32, %rsp
	testb	$1, %al
	je	.LBB19_5
.LBB19_4:                               # %if_then40
	movb	$1, %al
.LBB19_2:                               # %if_then38
	.seh_startepilogue
	movq	%rbp, %rsp
	popq	%rdi
	popq	%rsi
	popq	%rbp
	.seh_endepilogue
	retq
.LBB19_5:                               # %if_end41
	movl	$16, %eax
	callq	__chkstk
	subq	%rax, %rsp
	movq	%rsp, %rdi
	movq	$2, (%rdi)
	.p2align	4
.LBB19_6:                               # %while_cond42
                                        # =>This Inner Loop Header: Depth=1
	movq	(%rdi), %rcx
	subq	$32, %rsp
	movq	%rcx, %rdx
	callq	S64___mul__
	addq	$32, %rsp
	movq	-8(%rbp), %rdx
	subq	$32, %rsp
	movq	%rax, %rcx
	callq	S64___le__
	addq	$32, %rsp
	testb	$1, %al
	je	.LBB19_4
# %bb.7:                                # %while_body43
                                        #   in Loop: Header=BB19_6 Depth=1
	movq	-8(%rbp), %rsi
	movq	(%rdi), %rdx
	subq	$32, %rsp
	movq	%rsi, %rcx
	callq	S64___floordiv__
	addq	$32, %rsp
	movq	(%rdi), %rdx
	subq	$32, %rsp
	movq	%rax, %rcx
	callq	S64___mul__
	movq	%rsi, %rcx
	movq	%rax, %rdx
	callq	S64___sub__
	movq	%rax, %rcx
	xorl	%edx, %edx
	callq	S64___eq__
	addq	$32, %rsp
	testb	$1, %al
	je	.LBB19_6
	jmp	.LBB19_1
	.seh_endproc
                                        # -- End function
	.def	start;
	.scl	2;
	.type	32;
	.endef
	.globl	__real@40091eb851eb851f         # -- Begin function start
	.section	.rdata,"dr",discard,__real@40091eb851eb851f
	.p2align	3, 0x0
__real@40091eb851eb851f:
	.quad	0x40091eb851eb851f              # double 3.1400000000000001
	.globl	__real@4006e147ae147ae1
	.section	.rdata,"dr",discard,__real@4006e147ae147ae1
	.p2align	3, 0x0
__real@4006e147ae147ae1:
	.quad	0x4006e147ae147ae1              # double 2.8599999999999999
	.text
	.globl	start
	.p2align	4
start:                                  # @start
.seh_proc start
# %bb.0:                                # %entry
	subq	$184, %rsp
	.seh_stackalloc 184
	.seh_endprologue
	movl	$10, %ecx
	movl	$20, %edx
	callq	add_s64
	movq	%rax, 176(%rsp)
	movl	$50, %ecx
	movl	$30, %edx
	callq	sub_s64
	movq	%rax, 168(%rsp)
	movl	$5, %ecx
	movl	$6, %edx
	callq	mul_s64
	movq	%rax, 160(%rsp)
	movl	$100, %ecx
	movl	$4, %edx
	callq	div_s64
	movq	%rax, 152(%rsp)
	movsd	__real@40091eb851eb851f(%rip), %xmm0 # xmm0 = [3.1400000000000001E+0,0.0E+0]
	movsd	__real@4006e147ae147ae1(%rip), %xmm1 # xmm1 = [2.8599999999999999E+0,0.0E+0]
	callq	add_f64
	movsd	%xmm0, 144(%rsp)
	movl	$42, %ecx
	callq	is_positive
	andb	$1, %al
	movb	%al, 47(%rsp)
	movl	$5, %ecx
	movl	$5, %edx
	callq	is_equal
	andb	$1, %al
	movb	%al, 46(%rsp)
	movl	$10, %ecx
	movl	$20, %edx
	callq	max_s64
	movq	%rax, 136(%rsp)
	movl	$10, %ecx
	movl	$20, %edx
	callq	min_s64
	movq	%rax, 128(%rsp)
	movq	$-42, %rcx
	callq	abs_s64
	movq	%rax, 120(%rsp)
	movl	$150, %ecx
	movl	$100, %r8d
	xorl	%edx, %edx
	callq	clamp
	movq	%rax, 112(%rsp)
	movl	$5, %ecx
	callq	factorial
	movq	%rax, 104(%rsp)
	movl	$10, %ecx
	callq	fibonacci
	movq	%rax, 96(%rsp)
	movl	$10, %ecx
	callq	sum_to_n
	movq	%rax, 88(%rsp)
	movl	$5, %ecx
	callq	factorial_iter
	movq	%rax, 80(%rsp)
	movl	$12345, %ecx                    # imm = 0x3039
	callq	count_digits
	movq	%rax, 72(%rsp)
	movl	$1, %ecx
	movl	$5, %edx
	callq	sum_range
	movq	%rax, 64(%rsp)
	movl	$2, %ecx
	movl	$10, %edx
	callq	power
	movq	%rax, 56(%rsp)
	movl	$48, %ecx
	movl	$18, %edx
	callq	gcd
	movq	%rax, 48(%rsp)
	movl	$17, %ecx
	callq	is_prime
	andb	$1, %al
	movb	%al, 45(%rsp)
	.seh_startepilogue
	addq	$184, %rsp
	.seh_endepilogue
	retq
	.seh_endproc
                                        # -- End function
	.globl	_fltused
