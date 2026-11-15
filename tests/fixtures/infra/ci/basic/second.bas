' Waits one second by the clock, then prints. The clock wraps at midnight, which ends the wait too.
start! = TIMER
DO
LOOP UNTIL TIMER - start! >= 1 OR TIMER < start!
PRINT "done"
END
